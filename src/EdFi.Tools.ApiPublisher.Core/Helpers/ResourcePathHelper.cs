// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Core.Helpers
{
	public static class ResourcePathHelper
    {
        /// <summary>
        /// Parses the supplied text as CSV (comma separated values) into an array, removing
        /// leading and trailing whitespace from each item.
        /// </summary>
        /// <param name="resourcesCsv">The text to be parsed to an array.</param>
        /// <returns>An array of values from the parsed items.</returns>
        public static string[] ParseResourcesCsvToResourcePathArray(string resourcesCsv)
        {
            if (string.IsNullOrWhiteSpace(resourcesCsv))
            {
                return Array.Empty<string>();
            }

            return resourcesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Select(NormalizeResourcePath)
                .ToArray();
        }

        private static string NormalizeResourcePath(string resource)
        {
            // Look for "default" format (an Ed-Fi resource)
            if (!resource.Contains("/"))
            {
                return $"/ed-fi/{resource}";
            }

            var resourceParts = resource.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (resourceParts.Length != 2)
            {
                throw new Exception($"Invalid resource name format used for '{resource}'. Expected format is '{{schema}}/{{resource}}', with the resource name pluralized.");
            }

            return $"/{resourceParts[0]}/{resourceParts[1]}";
        }

        public static string GetResourcePath(string resourceKey)
        {
            return resourceKey.Split('#')[0];
        }

        public static bool IsDescriptor(string resourceUrl)
        {
            return resourceUrl.Split('?')[0].EndsWith("Descriptors");
        }

        public static Dictionary<string, int> ParseResourcePageSizeCsvToDictionary(string resourcePageSizeCsv)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(resourcePageSizeCsv))
            {
                foreach (var pair in ParseResourcesCsvToResourcePathArray(resourcePageSizeCsv))
                {
                    var parts = pair.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int size))
                    {
                        result[parts[0].Trim()] = size;
                        result[parts[0].Trim() + "/deletes"] = size;
                        result[parts[0].Trim() + "/keyChanges"] = size;
                    }
                    else
                    {
                        throw new Exception($"Invalid resource page size name format used for '{pair}'. Expected format is '<string>:<int>' '{{resource}}:{{page_size}}', with the resource name pluralized.");
                    }
                }
            }
            return result;
        }
    }
}
