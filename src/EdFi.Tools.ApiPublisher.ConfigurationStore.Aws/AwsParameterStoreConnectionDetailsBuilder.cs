// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.Aws;

/// <summary>
/// Builds <see cref="ApiConnectionDetails"/> from AWS Systems Manager-backed configuration,
/// including optional consolidated <c>credentials</c> JSON (SecureString) merged with legacy flat parameters.
/// </summary>
public static class AwsParameterStoreConnectionDetailsBuilder
{
    internal const string CredentialsConfigurationKey = "credentials";

    /// <summary>
    /// Binds <see cref="ApiConnectionDetails"/> from connection-scoped configuration, merges <c>credentials</c> JSON when present,
    /// assigns the connection name, and validates that Url, Key, and Secret are defined.
    /// </summary>
    public static ApiConnectionDetails Build(IConfiguration configuration, string apiConnectionName)
    {
        var connectionDetails = configuration.Get<ApiConnectionDetails>() ?? new ApiConnectionDetails();

        var connectionPrefix = ConfigurationStoreHelper.Key(apiConnectionName);
        var credentialsParameterPath = $"{connectionPrefix}/{CredentialsConfigurationKey}";

        ApplyCredentialsJsonIfPresent(
            connectionDetails,
            configuration[CredentialsConfigurationKey],
            credentialsParameterPath);

        connectionDetails.Name = apiConnectionName;

        if (!connectionDetails.IsFullyDefined())
        {
            throw new Exception(
                $"API connection '{apiConnectionName}' is missing required Url, Key, or Secret. "
                + $"Provide them in SecureString parameter '{credentialsParameterPath}' as JSON with "
                + "\"url\", \"key\", and \"secret\" properties, or use legacy parameters "
                + $"'{connectionPrefix}/url', '{connectionPrefix}/key', and '{connectionPrefix}/secret'.");
        }

        return connectionDetails;
    }

    internal static void ApplyCredentialsJsonIfPresent(
        ApiConnectionDetails connectionDetails,
        string credentialsJson,
        string credentialsParameterFullPath)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return;
        }

        JObject obj;

        try
        {
            obj = JObject.Parse(credentialsJson.Trim());
        }
        catch (JsonException ex)
        {
            throw new Exception(
                $"Unable to parse AWS Parameter Store JSON for '{credentialsParameterFullPath}'.",
                ex);
        }

        if (TryGetNonEmptyStringProperty(obj, "url", out var url))
        {
            connectionDetails.Url = url;
        }

        if (TryGetNonEmptyStringProperty(obj, "key", out var key))
        {
            connectionDetails.Key = key;
        }

        if (TryGetNonEmptyStringProperty(obj, "secret", out var secret))
        {
            connectionDetails.Secret = secret;
        }
    }

    private static bool TryGetNonEmptyStringProperty(JObject obj, string propertyName, out string value)
    {
        value = null;

        var prop = obj.Properties()
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

        if (prop == null)
        {
            return false;
        }

        if (prop.Value.Type != JTokenType.String)
        {
            return false;
        }

        var s = prop.Value.Value<string>()?.Trim();

        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        value = s;
        return true;
    }
}
