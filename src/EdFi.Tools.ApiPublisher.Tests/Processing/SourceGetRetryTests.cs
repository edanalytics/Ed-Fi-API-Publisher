// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using System;
using EdFi.Tools.ApiPublisher.Tests;
using FakeItEasy;
using NUnit.Framework;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

[TestFixture]
public class SourceGetRetryTests
{
    private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";

    /// <summary>
    /// When the source total-count GET throws an exception (e.g. SSL / transport failure),
    /// Polly should retry and succeed on the next attempt so publishing can continue.
    /// </summary>
    [Test]
    public async Task When_source_total_count_GET_throws_HttpRequestException_then_retries_and_completes()
    {
        var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();
        var suppliedSourceResources = sourceResourceFaker.Generate(1);

        var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
            .AvailableChangeVersions(1100);

        int mainResourceTotalCountGetInvocations = 0;

        A.CallTo(() => fakeSourceRequestHandler.Get(
                A<string>.Ignored,
                A<HttpRequestMessage>.That.Matches(HasTotalCountParameter, "totalCount=true")))
            .ReturnsLazily((string _, HttpRequestMessage request) =>
            {
                var localPath = request.RequestUri.LocalPath;
                var isMainResourceTotalCount =
                    localPath.Contains("/ed-fi/stateEducationAgencies", StringComparison.OrdinalIgnoreCase)
                    && !localPath.Contains("/deletes", StringComparison.OrdinalIgnoreCase);

                if (isMainResourceTotalCount)
                {
                    mainResourceTotalCountGetInvocations++;
                    if (mainResourceTotalCountGetInvocations == 1)
                    {
                        throw CreateSslLikeHttpRequestException();
                    }
                }

                return FakeResponse.OK("[]").AppendHeaders(("Total-Count", "1"));
            });

        fakeSourceRequestHandler.GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}", suppliedSourceResources);

        var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
        fakeTargetRequestHandler.PostResource($"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}", HttpStatusCode.OK);

        var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(include: new[] { StateEducationAgencies });
        var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

        var options = TestHelpers.GetOptions();
        options.IncludeDescriptors = false;

        TestHelpers.InitializeLogging();

        var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);
        var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
            options,
            sourceApiConnectionDetails,
            fakeSourceRequestHandler,
            targetApiConnectionDetails,
            fakeTargetRequestHandler);

        await changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None);

        Assert.That(mainResourceTotalCountGetInvocations, Is.EqualTo(2), "Total-count GET should run twice (fail once, retry once).");

        A.CallTo(() => fakeTargetRequestHandler.Post(
                $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{StateEducationAgencies}",
                A<HttpRequestMessage>.Ignored))
            .MustHaveHappened(1, Times.Exactly);
    }

    private static bool HasTotalCountParameter(HttpRequestMessage request)
    {
        var queryString = request.RequestUri.ParseQueryString();
        return queryString["totalCount"] == "true";
    }

    private static HttpRequestException CreateSslLikeHttpRequestException()
    {
        return new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));
    }
}
