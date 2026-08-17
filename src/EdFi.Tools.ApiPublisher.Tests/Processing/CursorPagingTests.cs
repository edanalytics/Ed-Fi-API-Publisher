// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing;

[TestFixture]
public class CursorPagingTests
{
    private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";
    private const string Students = "/ed-fi/students";

    [Test]
    public async Task Handler_when_cursor_paging_follows_next_page_token_and_uses_page_size_query()
    {
        TestHelpers.InitializeLogging();

        var fakeSource = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

        var captured = new List<HttpRequestMessage>();
        int seq = 0;

        A.CallTo(() => fakeSource.Get(
                A<string>.Ignored,
                A<HttpRequestMessage>.That.Matches(m =>
                    m.RequestUri.LocalPath.EndsWith("/ed-fi/stateEducationAgencies", StringComparison.Ordinal)
                    && m.RequestUri.Query.Contains("pageSize", StringComparison.Ordinal)
                    && !m.RequestUri.LocalPath.Contains("partitions", StringComparison.Ordinal))))
            .Invokes((string _, HttpRequestMessage msg) => captured.Add(msg))
            .ReturnsLazily(() =>
            {
                seq++;
                if (seq == 1)
                {
                    return FakeResponse.OK("[{}]").AppendHeaders(("Next-Page-Token", "tokenB"));
                }

                return FakeResponse.OK("[]");
            });

        var sourceDetails = TestHelpers.GetSourceApiConnectionDetails();
        var client = new EdFiApiClient("Test", sourceDetails, 27, true, new HttpClientHandlerFakeBridge(fakeSource));
        var provider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(() => client));
        var handler = new EdFiApiStreamResourcePageMessageHandler(provider);

        var options = TestHelpers.GetOptions();
        options.UseCursorPaging = true;
        options.StreamingPageSize = 50;

        var errorBlock = new ActionBlock<ErrorItemMessage>(_ => { });

        int transformCalls = 0;
        var pageMessage = new StreamResourcePageMessage<PostItemMessage>
        {
            ResourceUrl = StateEducationAgencies,
            CursorStartPageToken = null,
            Offset = null,
            Limit = null,
            ChangeWindow = null,
            CancellationSource = new CancellationTokenSource(),
            CreateProcessDataMessages = (_, _) =>
            {
                transformCalls++;
                return Array.Empty<PostItemMessage>();
            },
        };

        await handler.HandleStreamResourcePageAsync(pageMessage, options, errorBlock);

        Assert.That(transformCalls, Is.EqualTo(2));
        Assert.That(captured.Count, Is.EqualTo(2));
        Assert.That(captured[0].RequestUri.Query, Does.Contain("pageSize=50"));
        Assert.That(captured[0].RequestUri.Query, Does.Not.Contain("pageToken"));
        Assert.That(captured[0].RequestUri.Query, Does.Not.Contain("offset"));
        Assert.That(captured[1].RequestUri.Query, Does.Contain("pageToken=tokenB"));
    }

    [Test]
    public async Task Producer_when_cursor_partition_count_zero_emits_single_message_without_partitions_request()
    {
        TestHelpers.InitializeLogging();

        var fakeSource = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

        var sourceDetails = TestHelpers.GetSourceApiConnectionDetails();
        var client = new EdFiApiClient("Test", sourceDetails, 27, true, new HttpClientHandlerFakeBridge(fakeSource));
        var provider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(() => client));
        var producer = new EdFiApiCursorPagingStreamResourcePageMessageProducer(provider, null);

        var options = TestHelpers.GetOptions();
        options.CursorPartitionCount = 0;

        var streamMessage = new StreamResourceMessage
        {
            ResourceUrl = Students,
            PageSize = 50,
            CancellationSource = new CancellationTokenSource(),
            Dependencies = Array.Empty<Task>(),
        };

        var errorBlock = new ActionBlock<ErrorItemMessage>(_ => { });

        var pages = await producer.ProduceMessagesAsync<PostItemMessage>(
            streamMessage,
            options,
            errorBlock,
            (_, _) => Array.Empty<PostItemMessage>(),
            CancellationToken.None);

        var list = pages.ToList();
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].CursorStartPageToken, Is.Null.Or.Empty);
        Assert.That(list[0].Offset, Is.Null);
        Assert.That(list[0].Limit, Is.Null);

        A.CallTo(() => fakeSource.Get(
                A<string>.Ignored,
                A<HttpRequestMessage>.That.Matches(m => m.RequestUri.LocalPath.Contains("partitions", StringComparison.Ordinal))))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task Producer_when_partitions_return_tokens_emits_one_message_per_token()
    {
        TestHelpers.InitializeLogging();

        var fakeSource = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

        A.CallTo(() => fakeSource.Get(
                A<string>.Ignored,
                A<HttpRequestMessage>.That.Matches(m =>
                    m.RequestUri.LocalPath.Contains("/partitions", StringComparison.Ordinal)
                    && m.RequestUri.LocalPath.Contains("/ed-fi/students", StringComparison.Ordinal))))
            .Returns(FakeResponse.OK(new { pageTokens = new[] { "aaa", "bbb" } }));

        var sourceDetails = TestHelpers.GetSourceApiConnectionDetails();
        var client = new EdFiApiClient("Test", sourceDetails, 27, true, new HttpClientHandlerFakeBridge(fakeSource));
        var provider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(() => client));
        var producer = new EdFiApiCursorPagingStreamResourcePageMessageProducer(provider, null);

        var options = TestHelpers.GetOptions();
        options.CursorPartitionCount = 5;

        var streamMessage = new StreamResourceMessage
        {
            ResourceUrl = Students,
            PageSize = 50,
            CancellationSource = new CancellationTokenSource(),
            Dependencies = Array.Empty<Task>(),
        };

        var errorBlock = new ActionBlock<ErrorItemMessage>(_ => { });

        var pages = (await producer.ProduceMessagesAsync<PostItemMessage>(
            streamMessage,
            options,
            errorBlock,
            (_, _) => Array.Empty<PostItemMessage>(),
            CancellationToken.None)).ToList();

        Assert.That(pages, Has.Count.EqualTo(2));
        Assert.That(pages[0].CursorStartPageToken, Is.EqualTo("aaa"));
        Assert.That(pages[1].CursorStartPageToken, Is.EqualTo("bbb"));
    }

    [Test]
    public async Task Producer_when_partitions_request_fails_falls_back_to_single_stream()
    {
        TestHelpers.InitializeLogging();

        var fakeSource = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

        A.CallTo(() => fakeSource.Get(
                A<string>.Ignored,
                A<HttpRequestMessage>.That.Matches(m =>
                    m.RequestUri.LocalPath.Contains("/partitions", StringComparison.Ordinal))))
            .Returns(FakeResponse.NotFound());

        var sourceDetails = TestHelpers.GetSourceApiConnectionDetails();
        var client = new EdFiApiClient("Test", sourceDetails, 27, true, new HttpClientHandlerFakeBridge(fakeSource));
        var provider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(() => client));
        var producer = new EdFiApiCursorPagingStreamResourcePageMessageProducer(provider, null);

        var options = TestHelpers.GetOptions();
        options.CursorPartitionCount = 3;

        var streamMessage = new StreamResourceMessage
        {
            ResourceUrl = Students,
            PageSize = 50,
            CancellationSource = new CancellationTokenSource(),
            Dependencies = Array.Empty<Task>(),
        };

        var errorBlock = new ActionBlock<ErrorItemMessage>(_ => { });

        var pages = (await producer.ProduceMessagesAsync<PostItemMessage>(
            streamMessage,
            options,
            errorBlock,
            (_, _) => Array.Empty<PostItemMessage>(),
            CancellationToken.None)).ToList();

        Assert.That(pages, Has.Count.EqualTo(1));
        Assert.That(pages[0].CursorStartPageToken, Is.Null.Or.Empty);
    }
}
