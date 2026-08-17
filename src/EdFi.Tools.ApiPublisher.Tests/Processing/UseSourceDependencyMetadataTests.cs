// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
	[TestFixture]
    public class UseSourceDependencyMetadataTests
    {
        [TestFixture]
        public class When_useSourceDependencyMetadata_is_enabled : TestFixtureAsyncBase
        {
            private ChangeProcessor _changeProcessor;
            private IFakeHttpRequestHandler _fakeSourceRequestHandler;
            private IFakeHttpRequestHandler _fakeTargetRequestHandler;
            private ChangeProcessorConfiguration _changeProcessorConfiguration;

            protected override async Task ArrangeAsync()
            {
                var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();
                var suppliedSourceResources = sourceResourceFaker.Generate(5);

                _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                    .Dependencies()
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: 1)
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                    .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                _fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
                _fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                    exclude: new[] { "schools" });

                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false;
                options.UseSourceDependencyMetadata = true;

                TestHelpers.InitializeLogging();

                _changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                _changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    _fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    _fakeTargetRequestHandler);

				await Task.Yield();
			}

            protected override async Task ActAsync()
            {
                await _changeProcessor.ProcessChangesAsync(_changeProcessorConfiguration, CancellationToken.None);
            }

            [Test]
            public void Should_request_dependency_metadata_from_the_source_api()
            {
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}/metadata/{MockRequests.DataManagementPath.TrimStart('/')}/dependencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [Test]
            public void Should_not_request_dependency_metadata_from_the_target_api()
            {
                A.CallTo(
                        () => _fakeTargetRequestHandler.Get(
                            $"{MockRequests.TargetApiBaseUrl}/metadata/{MockRequests.DataManagementPath.TrimStart('/')}/dependencies",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }

            [TestCase("/ed-fi/localEducationAgencies")]
            public void Should_still_apply_resource_exclusions_using_source_dependency_metadata(string resourceCollectionUrl)
            {
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustHaveHappened();
            }

            [TestCase("/ed-fi/schools")]
            public void Should_not_attempt_to_publish_excluded_resources(string resourceCollectionUrl)
            {
                A.CallTo(
                        () => _fakeSourceRequestHandler.Get(
                            $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{resourceCollectionUrl}",
                            A<HttpRequestMessage>.Ignored))
                    .MustNotHaveHappened();
            }
        }
    }
}
