// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimit;
using Polly.RateLimiting;
using Polly.Retry;
using Serilog;
using Serilog.Events;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;

public class EdFiApiCursorPagingStreamResourcePageMessageProducer : IStreamResourcePageMessageProducer
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;
    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiCursorPagingStreamResourcePageMessageProducer));

    public EdFiApiCursorPagingStreamResourcePageMessageProducer(
        ISourceEdFiApiClientProvider sourceEdFiApiClientProvider,
        IRateLimiting<HttpResponseMessage> rateLimiter = null)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
        _rateLimiter = rateLimiter;
    }

    public async Task<IEnumerable<StreamResourcePageMessage<TProcessDataMessage>>> ProduceMessagesAsync<TProcessDataMessage>(
        StreamResourceMessage message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        CancellationToken cancellationToken)
    {
        _logger.Information($"{message.ResourceUrl}: Preparing cursor paging (no total count).");

        IReadOnlyList<string> partitionTokens = Array.Empty<string>();

        if (options.CursorPartitionCount > 0)
        {
            partitionTokens = await TryGetPartitionPageTokensAsync(
                    message.ResourceUrl,
                    message.ChangeWindow,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (partitionTokens.Count == 0)
        {
            return new[]
            {
                CreatePageMessage<TProcessDataMessage>(message, createProcessDataMessages, cursorStartPageToken: null)
            };
        }

        return partitionTokens
            .Select(t => CreatePageMessage<TProcessDataMessage>(message, createProcessDataMessages, t))
            .ToList();
    }

    private static StreamResourcePageMessage<TProcessDataMessage> CreatePageMessage<TProcessDataMessage>(
        StreamResourceMessage message,
        Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        string cursorStartPageToken)
    {
        return new StreamResourcePageMessage<TProcessDataMessage>
        {
            ResourceUrl = message.ResourceUrl,
            PostAuthorizationFailureRetry = message.PostAuthorizationFailureRetry,
            Offset = null,
            Limit = null,
            CursorStartPageToken = cursorStartPageToken,
            IsFinalPage = false,
            ChangeWindow = message.ChangeWindow,
            CreateProcessDataMessages = createProcessDataMessages,
            CancellationSource = message.CancellationSource,
        };
    }

    private async Task<IReadOnlyList<string>> TryGetPartitionPageTokensAsync(
        string resourceUrl,
        ChangeWindow changeWindow,
        Options options,
        CancellationToken cancellationToken)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();
        string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(changeWindow);

        string requestUri =
            $"{edFiApiClient.DataManagementApiSegment}{resourceUrl}/partitions?number={options.CursorPartitionCount}{changeWindowQueryStringParameters}";

        var delay = Backoff.ExponentialBackoff(
            TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
            options.MaxRetryAttempts);

        bool isRateLimitingEnabled = options.EnableRateLimit;

        var retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
            .WaitAndRetryAsync(
                delay,
                (result, ts, retryAttempt, ctx) =>
                {
                    if (result.Exception != null)
                    {
                        _logger.Warning(
                            $"{resourceUrl}: GET partitions failed with an exception (retry #{retryAttempt}):{Environment.NewLine}{result.Exception}");
                    }
                    else
                    {
                        _logger.Warning(
                            $"{resourceUrl}: GET partitions failed with status '{result.Result.StatusCode}' (retry #{retryAttempt}).");
                    }
                });

        IAsyncPolicy<HttpResponseMessage> policy =
            isRateLimitingEnabled ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy) : retryPolicy;

        try
        {
            var apiResponse = await policy
                .ExecuteAsync(
                    (ctx, ct) => RequestHelpers.SendGetRequestAsync(edFiApiClient, resourceUrl, requestUri, ct),
                    new Context(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!apiResponse.IsSuccessStatusCode || apiResponse.Content == null)
            {
                _logger.Warning(
                    $"{resourceUrl}: Partitions request was not successful ({apiResponse.StatusCode}). Falling back to single cursor stream.");

                return Array.Empty<string>();
            }

            string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var root = JObject.Parse(responseContent);
            var tokens = root["pageTokens"] as JArray;

            if (tokens == null || tokens.Count == 0)
            {
                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug($"{resourceUrl}: Partitions response contained no pageTokens. Falling back to single cursor stream.");
                }

                return Array.Empty<string>();
            }

            return tokens.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        catch (RateLimitRejectedException)
        {
            _logger.Fatal($"{resourceUrl}: Rate limit exceeded while requesting partitions.");

            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, $"{resourceUrl}: Error requesting partitions. Falling back to single cursor stream.");

            return Array.Empty<string>();
        }
    }
}
