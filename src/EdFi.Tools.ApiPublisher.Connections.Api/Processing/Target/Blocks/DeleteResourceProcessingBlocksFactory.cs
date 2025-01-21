// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimit;
using Polly.RateLimiting;
using Polly.Retry;
using Serilog;
using Serilog.Events;
using System.Net;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks
{
    /// <summary>
    /// Builds a pipeline that processes deletes against a target Ed-Fi ODS API.
    /// </summary>
    /// <remarks>Receives a <see cref="GetItemForDeletionMessage" />, transforms to a <see cref="DeleteItemMessage" /> before
    /// producing <see cref="ErrorItemMessage" /> instances as output.</remarks>
    public class DeleteResourceProcessingBlocksFactory : IProcessingBlocksFactory<GetItemForDeletionMessage>
    {
        private readonly ITargetEdFiApiClientProvider _targetEdFiApiClientProvider;
        private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;
        private static readonly ILogger _logger = Log.Logger.ForContext(typeof(DeleteResourceProcessingBlocksFactory));

        public DeleteResourceProcessingBlocksFactory(ITargetEdFiApiClientProvider targetEdFiApiClientProvider, IRateLimiting<HttpResponseMessage> rateLimiter =null)
        {
            _targetEdFiApiClientProvider = targetEdFiApiClientProvider;
            _rateLimiter = rateLimiter;
        }

        public (ITargetBlock<GetItemForDeletionMessage>, ISourceBlock<ErrorItemMessage>) CreateProcessingBlocks(
            CreateBlocksRequest createBlocksRequest)
        {
            TransformManyBlock<GetItemForDeletionMessage, DeleteItemMessage> getItemForDeletionBlock =
                CreateGetItemForDeletionBlock(
                    _targetEdFiApiClientProvider.GetApiClient(),
                    createBlocksRequest.Options,
                    createBlocksRequest.ErrorHandlingBlock);

            TransformManyBlock<DeleteItemMessage, ErrorItemMessage> deleteResourceBlock
                = CreateDeleteResourceBlock(_targetEdFiApiClientProvider.GetApiClient(), createBlocksRequest.Options);

            getItemForDeletionBlock.LinkTo(deleteResourceBlock, new DataflowLinkOptions { PropagateCompletion = true });

            return (getItemForDeletionBlock, deleteResourceBlock);
        }

        private TransformManyBlock<GetItemForDeletionMessage, DeleteItemMessage> CreateGetItemForDeletionBlock(
            EdFiApiClient targetApiClient,
            Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var getItemForDeletionBlock = new TransformManyBlock<GetItemForDeletionMessage, DeleteItemMessage>(
                async msg =>
                {
                    // If the message wasn't created (because there is no natural key information)
                    if (msg == null)
                    {
                        return Enumerable.Empty<DeleteItemMessage>();
                    }

                    string id = msg.Id;

                    try
                    {
                        var keyValueParms = msg.KeyValues
                            .OfType<JProperty>()
                            .Select(p => $"{p.Name}={WebUtility.UrlEncode(GetQueryStringValue(p))}");

                        string queryString = String.Join("&", keyValueParms);

                        var delay = Backoff.ExponentialBackoff(
                            TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                            options.MaxRetryAttempts);

                        int attempts = 0;
                        // Rate Limit
                        bool isRateLimitingEnabled = options.EnableRateLimit;
                        
                        var retryPolicy = Policy
                            .Handle<Exception>()
                            .OrResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                            .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                            {
                                if (result.Exception != null)
                                {
                                    _logger.Warning($"{msg.ResourceUrl} (source id: {id}): GET by key for deletion of target resource attempt #{attempts}): {result.Exception}");
                                }
                                else
                                {
                                    _logger.Warning(
                                        $"{msg.ResourceUrl} (source id: {id}): GET by key for deletion of target resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                                }
                            });

                        IAsyncPolicy<HttpResponseMessage> policy = isRateLimitingEnabled ? Policy.WrapAsync(_rateLimiter.GetRateLimitingPolicy(), retryPolicy) : retryPolicy;
                        var apiResponse = await policy.ExecuteAsync((ctx, ct) =>
                        {
                            attempts++;

                            if (attempts > 1)
                            {
                                if (_logger.IsEnabled(LogEventLevel.Debug))
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion of target resource (attempt #{attempts}) using '{queryString}'...");
                                }
                            }

                            return targetApiClient.HttpClient.GetAsync($"{targetApiClient.DataManagementApiSegment}{msg.ResourceUrl}?{queryString}", ct);
                        }, new Context(), CancellationToken.None);

                        string responseContent = null;

                        responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (!apiResponse.IsSuccessStatusCode)
                        {
                            _logger.Error(
                                $"{msg.ResourceUrl} (source id: {id}): GET by key returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                            var error = new ErrorItemMessage
                            {
                                Method = HttpMethod.Get.ToString(),
                                ResourceUrl = $"{msg.ResourceUrl}?{queryString}",
                                Id = id,
                                Body = null,
                                ResponseStatus = apiResponse.StatusCode,
                                ResponseContent = responseContent
                            };

                            // Publish the failure
                            errorHandlingBlock.Post(error);

                            // No delete to process
                            return Enumerable.Empty<DeleteItemMessage>();
                        }

                        // Success

                        // Log a message if this was successful after a retry.
                        if (_logger.IsEnabled(LogEventLevel.Information) && attempts > 1)
                        {
                            _logger.Information(
                                $"{msg.ResourceUrl} (source id: {id}): GET by key attempt #{attempts} returned {apiResponse.StatusCode}.");
                        }

                        if (_logger.IsEnabled(LogEventLevel.Debug))
                        {
                            _logger.Debug($"{msg.ResourceUrl} (source id: {id}): GET by key returned {apiResponse.StatusCode}");
                        }

                        var getByKeyResults = JArray.Parse(responseContent);

                        // If the item to be deleted cannot be found...
                        if (getByKeyResults.Count == 0)
                        {
                            if (_logger.IsEnabled(LogEventLevel.Debug))
                            {
                                _logger.Debug($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion returned no results on target API ({queryString}).");
                            }

                            return Enumerable.Empty<DeleteItemMessage>();
                        }

                        // Check if any query parameter is 0.  If yes, perforform fuzzy match item selection.  If not, select the first item in the response list.
                        bool hasZero = msg.KeyValues.OfType<JProperty>().Any(p =>
                            (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float) &&
                            (double)p.Value == 0);
                        
                        JObject matchingResult = null;

                        if (hasZero) {
                            _logger.Warning($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion query included a 0 value.");
                            // Find a result that matches the msg query...
                            matchingResult = getByKeyResults
                                .OfType<JObject>()
                                .FirstOrDefault(item => MatchesQuery(item, msg));

                            if (matchingResult == null) {
                                _logger.Warning($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion returned {getByKeyResults.Count} results on target API, and none matched. ({queryString})");

                                return Enumerable.Empty<DeleteItemMessage>();
                            }

                            // Log the query string and selected id.
                            _logger.Warning($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion returned {getByKeyResults.Count} results on target API. ({queryString})");
                            _logger.Warning($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion selected {matchingResult["id"].Value<string>()} to be deleted. ({matchingResult.ToString()})");

                        } else {
                            matchingResult = getByKeyResults[0] as JObject;
                        }

                        return new[]
                        {
                            new DeleteItemMessage
                            {
                                ResourceUrl = msg.ResourceUrl,
                                Id = matchingResult["id"].Value<string>(),
                                SourceId = msg.Id,
                            }
                        };
                    }
                    catch (RateLimitRejectedException)
                    {
                        _logger.Fatal($"{msg.ResourceUrl}: Rate limit exceeded. Please try again later.");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{msg.ResourceUrl} (source id: {id}): An unhandled exception occurred in the GetItemForDeletion block: {ex}");
                        throw;
                    }
                }, new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
                });

            return getItemForDeletionBlock;

            string GetQueryStringValue(JProperty property)
            {
                var type = property.Value.Type;

                switch (type)
                {
                    case JTokenType.Date:
                        var dateValue = property.Value.Value<DateTime>();

                        if (dateValue == dateValue.Date)
                        {
                            return dateValue.ToString("yyyy-MM-dd");
                        }

                        return JsonConvert.SerializeObject(property.Value).Trim('"');
                    case JTokenType.TimeSpan:
                        return JsonConvert.SerializeObject(property.Value).Trim('"');
                    default:
                        return property.Value.ToString();
                }
            }
        }

        // Function to compare a returned item with expected values
        private static bool MatchesQuery(JObject item, GetItemForDeletionMessage msg)
        {
            // Get all properties from the item that are strings or numbers
            List<JProperty> ItemProperties = GetStringOrNumberProperties(item);

            // Test that all properties from the query message are found in the item
            bool allExist = msg.KeyValues.OfType<JProperty>().All(jp1 =>
                ItemProperties.Any(jp2 => jp1.Name == jp2.Name && JToken.DeepEquals(jp1.Value, jp2.Value)));

            return allExist;
        }

        private static List<JProperty> GetStringOrNumberProperties(JObject jObject)
        {
            List<JProperty> result = new List<JProperty>();

            foreach (var property in jObject.Properties())
            {
                // Check the type of the value
                if (property.Value.Type == JTokenType.String || property.Value.Type == JTokenType.Integer || property.Value.Type == JTokenType.Float)
                {
                    result.Add(property);
                }
                else if (property.Value.Type == JTokenType.Object)
                {
                    // Recursively process nested JObjects
                    result.AddRange(GetStringOrNumberProperties((JObject)property.Value));
                }
                // Skip arrays and other types
            }

            return result;
        }

        private TransformManyBlock<DeleteItemMessage, ErrorItemMessage> CreateDeleteResourceBlock(
            EdFiApiClient targetApiClient, Options options)
        {
            var deleteResourceBlock = new TransformManyBlock<DeleteItemMessage, ErrorItemMessage>(
                async msg =>
            {
                string id = msg.Id;
                string sourceId = msg.SourceId;

                try
                {
                    var delay = Backoff.ExponentialBackoff(
                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                        options.MaxRetryAttempts);

                    int attempts = 0;
                    // Rate Limit
                    bool isRateLimitingEnabled = options.EnableRateLimit;
                    
                    var retryPolicy = Policy
                        .Handle<Exception>()
                        .OrResult<HttpResponseMessage>(r =>
                            r.StatusCode == HttpStatusCode.Conflict || r.StatusCode.IsPotentiallyTransientFailure())
                        .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                        {
                            if (result.Exception != null)
                            {
                                _logger.Warning($"{msg.ResourceUrl} (source id: {sourceId}): Delete resource attempt #{attempts} threw an exception: {result.Exception}");
                            }
                            else
                            {
                                _logger.Warning(
                                    $"{msg.ResourceUrl} (source id: {sourceId}): Delete resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                            }
                        });
                    IAsyncPolicy<HttpResponseMessage> policy = isRateLimitingEnabled ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy) : retryPolicy;
                    var apiResponse = await policy.ExecuteAsync((ctx, ct) =>
                        {
                            attempts++;

                            if (attempts > 1)
                            {
                                if (_logger.IsEnabled(LogEventLevel.Debug))
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): DELETE request (attempt #{attempts}.");
                                }
                            }

                            return targetApiClient.HttpClient.DeleteAsync($"{targetApiClient.DataManagementApiSegment}{msg.ResourceUrl}/{id}", ct);
                        }, new Context(), CancellationToken.None);

                    // Failure
                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                        _logger.Error(
                            $"{msg.ResourceUrl} (source id: {sourceId}): DELETE returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                        // Publish the failure
                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Delete.ToString(),
                            ResourceUrl = msg.ResourceUrl,
                            Id = id,
                            Body = null,
                            ResponseStatus = apiResponse.StatusCode,
                            ResponseContent = responseContent
                        };

                        return new[] { error };
                    }

                    // Success
                    if (_logger.IsEnabled(LogEventLevel.Information) && attempts > 1)
                    {
                        _logger.Information(
                            $"{msg.ResourceUrl} (source id: {sourceId}): DELETE attempt #{attempts} returned {apiResponse.StatusCode}.");
                    }

                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): DELETE returned {apiResponse.StatusCode}");
                    }

                    // Success - no errors to publish
                    return Enumerable.Empty<ErrorItemMessage>();
                }
                catch (RateLimitRejectedException)
                {
                    _logger.Fatal($"{msg.ResourceUrl}: Rate limit exceeded. Please try again later.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): An unhandled exception occurred in the DeleteResource block: {ex}");
                    throw;
                }
            }, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
            });

            return deleteResourceBlock;
        }

        public IEnumerable<GetItemForDeletionMessage> CreateProcessDataMessages(StreamResourcePageMessage<GetItemForDeletionMessage> message, string json)
        {
            JArray items = JArray.Parse(json);

            // Iterate through the page of items
            foreach (var item in items.OfType<JObject>())
            {
                // Stop processing individual items if cancellation has been requested
                if (message.CancellationSource.IsCancellationRequested)
                {
                    _logger.Debug(
                        $"{message.ResourceUrl}: Cancellation requested during item '{nameof(GetItemForDeletionMessage)}' creation.");

                    yield break;
                }

                // // Add the item to the buffer for processing into the target API
                // if (_logger.IsEnabled(LogEventLevel.Debug))
                // {
                //     _logger.Debug(
                //         $"{message.ResourceUrl}: Adding individual action message of type '{nameof(GetItemForDeletionMessage)}' for item '{item["id"]?.Value<string>() ?? "unknown"}'...");
                // }

                var itemMessage = CreateItemActionMessage(item);

                if (itemMessage == null)
                {
                    yield break;
                }

                yield return itemMessage;
            }

            GetItemForDeletionMessage CreateItemActionMessage(JObject obj)
            {
                // If there are no key values on the message, cancel delete processing since the source
                // API isn't providing the information to publish deletes between ODS API instances
                if (obj[EdFiApiConstants.KeyValuesPropertyName] == null)
                {
                    // Question: Should we add a flag for specifying that publishing without proper deletes support from source API is ok?
                    _logger.Warning($"Source API's '{EdFiApiConstants.DeletesPathSuffix}' response does not include the domain key values. Publishing of deletes to the target API cannot be performed.");
                    _logger.Debug("Attempting to gracefully cancel delete processing due to lack of support for deleted key values from the source API.");

                    message.CancellationSource.Cancel();

                    return null;
                }

                return new GetItemForDeletionMessage
                {
                    ResourceUrl = message.ResourceUrl.TrimSuffix(EdFiApiConstants.DeletesPathSuffix),
                    KeyValues = obj[EdFiApiConstants.KeyValuesPropertyName],
                    Id = obj["id"].Value<string>(),
                    CancellationToken = message.CancellationSource.Token,
                };
            }
        }
    }
}
