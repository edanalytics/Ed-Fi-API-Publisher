// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EdFi.Tools.ApiPublisher.ConfigurationStore.Aws;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.ConfigurationStore.Aws;

[TestFixture]
public class AwsParameterStoreConnectionDetailsBuilderTests
{
    private static IConfigurationRoot ConfigurationFromDictionary(Dictionary<string, string> values)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(values);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
    }

    [Test]
    public void Build_WhenFlatParametersOnly_UsesLegacyUrlKeySecret()
    {
        var cfg = ConfigurationFromDictionary(
            new Dictionary<string, string>
            {
                ["url"] = "https://api.example/",
                ["key"] = "client-id",
                ["secret"] = "client-secret",
            });

        var result = AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "MyConn");

        result.Name.Should().Be("MyConn");
        result.Url.Should().Be("https://api.example/");
        result.Key.Should().Be("client-id");
        result.Secret.Should().Be("client-secret");
    }

    [Test]
    public void Build_WhenCredentialsJsonOnly_UsesJsonValues()
    {
        var credentialsPayload = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["url"] = "https://json.example/",
                ["key"] = "k",
                ["secret"] = "s",
            });
        var cfg = ConfigurationFromDictionary(new Dictionary<string, string> { ["credentials"] = credentialsPayload });

        var result = AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "Conn");

        result.Url.Should().Be("https://json.example/");
        result.Key.Should().Be("k");
        result.Secret.Should().Be("s");
    }

    [Test]
    public void Build_WhenBothFlatAndCredentials_JsonNonEmptyFieldsOverrideFlat()
    {
        var credentialsPayload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = "https://from-json.example/" });
        var cfg = ConfigurationFromDictionary(
            new Dictionary<string, string>
            {
                ["url"] = "https://flat.example/",
                ["key"] = "flat-key",
                ["secret"] = "flat-secret",
                ["credentials"] = credentialsPayload,
            });

        var result = AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "Conn");

        result.Url.Should().Be("https://from-json.example/");
        result.Key.Should().Be("flat-key");
        result.Secret.Should().Be("flat-secret");
    }

    [Test]
    public void Build_WhenCredentialsWhitespaceOnly_LegacyFlatStillWorks()
    {
        var cfg = ConfigurationFromDictionary(
            new Dictionary<string, string>
            {
                ["url"] = "https://api.example/",
                ["key"] = "k",
                ["secret"] = "s",
                ["credentials"] = "   \t  ",
            });

        var result = AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "Conn");

        result.IsFullyDefined().Should().BeTrue();
    }

    [Test]
    public void Build_WhenCredentialsContainsInvalidJson_ThrowsWithParameterPath()
    {
        var cfg = ConfigurationFromDictionary(new Dictionary<string, string> { ["credentials"] = "{not valid json" });

        var ex = Assert.Throws<Exception>(() => AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "BadConn"));

        ex!.InnerException.Should().BeAssignableTo<Newtonsoft.Json.JsonException>();
        ex.Message.Should().Contain("/ed-fi/apiPublisher/connections/BadConn/credentials");
    }

    [Test]
    public void Build_WhenIncompleteDefinitions_ThrowsDescriptiveMessage()
    {
        var cfg = ConfigurationFromDictionary(new Dictionary<string, string> { ["url"] = "https://only-url.example/" });

        var ex = Assert.Throws<Exception>(() => AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "X"));

        ex!.Message.Should().Contain("missing required Url, Key, or Secret");
        ex.Message.Should().Contain("/credentials");
        ex.Message.Should().Contain("/url");
    }

    [Test]
    public void Build_WhenJsonUsesMixedCasePropertyNames_BindsCaseInsensitively()
    {
        var credentialsPayload = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["URL"] = "https://u.example/",
                ["Key"] = "kk",
                ["SECRET"] = "ss",
            });
        var cfg = ConfigurationFromDictionary(new Dictionary<string, string> { ["credentials"] = credentialsPayload });

        var result = AwsParameterStoreConnectionDetailsBuilder.Build(cfg, "C");

        result.Url.Should().Be("https://u.example/");
        result.Key.Should().Be("kk");
        result.Secret.Should().Be("ss");
    }
}
