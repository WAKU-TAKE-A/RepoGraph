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
                    "DotNetRuntimeEntrypoint", "DotNetHostBuilder", "AspNetStartupConfigureServices",
                    "AspNetStartupConfigure", "UiLifecycleEntrypoint", "SerializationAttributeCallback",
                    "SerializationJsonConverterCallback", "SerializationContractResolverCallback",
                    "MvvmToolkitMessageDispatch", "AutofacReflectionRegistration", "AutofacModuleLoad",
                    "ReflectionConstructorDispatch", "ReflectionConstructorCandidate",
                    "ServiceProviderDispatch", "AutofacResolveDispatch", "EventDispatch",
                    "FrameworkDelegateDispatch", "FrameworkDelegateFallbackCandidate",
                    "DelegateReference", "XamlEvent", "XamlActionBinding", "XamlCommandBinding",
                    "XamlNavigation"
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

                if (rules.TryGetValue("DotNetRuntimeEntrypoint", out var r1)) FrameworkRuleCatalog.DotNetRuntimeEntrypoint = r1;
                if (rules.TryGetValue("DotNetHostBuilder", out var r2)) FrameworkRuleCatalog.DotNetHostBuilder = r2;
                if (rules.TryGetValue("AspNetStartupConfigureServices", out var r3)) FrameworkRuleCatalog.AspNetStartupConfigureServices = r3;
                if (rules.TryGetValue("AspNetStartupConfigure", out var r4)) FrameworkRuleCatalog.AspNetStartupConfigure = r4;
                if (rules.TryGetValue("UiLifecycleEntrypoint", out var r5)) FrameworkRuleCatalog.UiLifecycleEntrypoint = r5;
                if (rules.TryGetValue("SerializationAttributeCallback", out var r6)) FrameworkRuleCatalog.SerializationAttributeCallback = r6;
                if (rules.TryGetValue("SerializationJsonConverterCallback", out var r7)) FrameworkRuleCatalog.SerializationJsonConverterCallback = r7;
                if (rules.TryGetValue("SerializationContractResolverCallback", out var r8)) FrameworkRuleCatalog.SerializationContractResolverCallback = r8;
                if (rules.TryGetValue("MvvmToolkitMessageDispatch", out var r9)) FrameworkRuleCatalog.MvvmToolkitMessageDispatch = r9;
                if (rules.TryGetValue("AutofacReflectionRegistration", out var r10)) FrameworkRuleCatalog.AutofacReflectionRegistration = r10;
                if (rules.TryGetValue("AutofacModuleLoad", out var r11)) FrameworkRuleCatalog.AutofacModuleLoad = r11;
                if (rules.TryGetValue("ReflectionConstructorDispatch", out var r12)) FrameworkRuleCatalog.ReflectionConstructorDispatch = r12;
                if (rules.TryGetValue("ReflectionConstructorCandidate", out var r13)) FrameworkRuleCatalog.ReflectionConstructorCandidate = r13;
                if (rules.TryGetValue("ServiceProviderDispatch", out var r14)) FrameworkRuleCatalog.ServiceProviderDispatch = r14;
                if (rules.TryGetValue("AutofacResolveDispatch", out var r15)) FrameworkRuleCatalog.AutofacResolveDispatch = r15;
                if (rules.TryGetValue("EventDispatch", out var r16)) FrameworkRuleCatalog.EventDispatch = r16;
                if (rules.TryGetValue("FrameworkDelegateDispatch", out var r17)) FrameworkRuleCatalog.FrameworkDelegateDispatch = r17;
                if (rules.TryGetValue("FrameworkDelegateFallbackCandidate", out var r18)) FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate = r18;
                if (rules.TryGetValue("DelegateReference", out var r19)) FrameworkRuleCatalog.DelegateReference = r19;
                if (rules.TryGetValue("XamlEvent", out var r20)) FrameworkRuleCatalog.XamlEvent = r20;
                if (rules.TryGetValue("XamlActionBinding", out var r21)) FrameworkRuleCatalog.XamlActionBinding = r21;
                if (rules.TryGetValue("XamlCommandBinding", out var r22)) FrameworkRuleCatalog.XamlCommandBinding = r22;
                if (rules.TryGetValue("XamlNavigation", out var r23)) FrameworkRuleCatalog.XamlNavigation = r23;
            }
        }
    }
}
