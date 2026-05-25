using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Probe.Services.Analysis
{
    internal static class FrameworkRuleCatalogLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static bool TryLoad(string filePath, out Dictionary<string, FrameworkRuleMetadata> rules)
        {
            rules = new Dictionary<string, FrameworkRuleMetadata>();
            try
            {
                if (!File.Exists(filePath))
                {
                    // No warning if file doesn't exist, just use defaults.
                    return false;
                }

                var json = File.ReadAllText(filePath);
                var loadedRules = JsonSerializer.Deserialize<Dictionary<string, FrameworkRuleMetadata>>(json, JsonOptions);
                
                if (loadedRules != null)
                {
                    rules = loadedRules;
                    return true;
                }
            }
            catch (JsonException jex)
            {
                Console.WriteLine($"[Warning] JSON format or schema error in {filePath}: {jex.Message}. Using built-in defaults.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to load rules from {filePath}: {ex.Message}. Using built-in defaults.");
            }

            return false;
        }

        public static void Initialize(string rulesFilePath)
        {
            if (TryLoad(rulesFilePath, out var rules))
            {
                var expectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ReflectionConstructorDispatch", "ReflectionConstructorCandidate"
                };

                foreach (var key in expectedKeys)
                {
                    if (!rules.ContainsKey(key))
                    {
                        Console.WriteLine($"[Warning] Missing framework rule key in JSON: {key}. Using built-in default.");
                    }
                }

                foreach (var key in rules.Keys)
                {
                    if (!expectedKeys.Contains(key))
                    {
                        Console.WriteLine($"[Warning] Unknown framework rule key in JSON: {key}. Ignoring.");
                    }
                }

                if (rules.TryGetValue("ReflectionConstructorDispatch", out var r12)) FrameworkRuleCatalog.ReflectionConstructorDispatch = r12;
                if (rules.TryGetValue("ReflectionConstructorCandidate", out var r13)) FrameworkRuleCatalog.ReflectionConstructorCandidate = r13;
            }
        }
    }
}
