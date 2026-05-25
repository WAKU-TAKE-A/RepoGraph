using System;

namespace Probe.Services.Analysis
{
    internal enum ExtractionRuleMode
    {
        HardEdge,
        Candidate
    }

    internal sealed record FrameworkRuleMetadata(
        string RuleId,
        string Family,
        ExtractionRuleMode Mode,
        string CallType)
    {
        public string ModeName => Mode.ToString().ToLowerInvariant();
    }

    internal static class FrameworkRuleCatalog
    {
        static FrameworkRuleCatalog()
        {
            try
            {
                var exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                var rulesPath = System.IO.Path.Combine(exeDir, "rules", "framework_rules.json");
                FrameworkRuleCatalogLoader.Initialize(rulesPath);
            }
            catch { }
        }

        public static FrameworkRuleMetadata DotNetRuntimeEntrypoint { get; set; } =
            new("dotnet.runtime.entrypoint", "dotnet", ExtractionRuleMode.HardEdge, "lifecycle_entrypoint");

        public static FrameworkRuleMetadata DotNetHostBuilder { get; set; } =
            new("dotnet.host_builder.entrypoint", "dotnet", ExtractionRuleMode.HardEdge, "lifecycle_entrypoint");

        public static FrameworkRuleMetadata AspNetStartupConfigureServices { get; set; } =
            new("aspnet.startup.configure_services", "aspnet", ExtractionRuleMode.HardEdge, "lifecycle_entrypoint");

        public static FrameworkRuleMetadata AspNetStartupConfigure { get; set; } =
            new("aspnet.startup.configure", "aspnet", ExtractionRuleMode.HardEdge, "lifecycle_entrypoint");

        public static FrameworkRuleMetadata UiLifecycleEntrypoint { get; set; } =
            new("ui.lifecycle.entrypoint", "ui", ExtractionRuleMode.HardEdge, "lifecycle_entrypoint");

        public static FrameworkRuleMetadata SerializationAttributeCallback { get; set; } =
            new("serialization.attribute.callback", "serialization", ExtractionRuleMode.HardEdge, "serialization_callback");

        public static FrameworkRuleMetadata SerializationJsonConverterCallback { get; set; } =
            new("serialization.json_converter.callback", "serialization", ExtractionRuleMode.HardEdge, "serialization_callback");

        public static FrameworkRuleMetadata SerializationContractResolverCallback { get; set; } =
            new("serialization.contract_resolver.callback", "serialization", ExtractionRuleMode.HardEdge, "serialization_callback");

        public static FrameworkRuleMetadata MvvmToolkitMessageDispatch { get; set; } =
            new("mvvm.toolkit.message_dispatch", "mvvm", ExtractionRuleMode.HardEdge, "mvvm_toolkit_message_dispatch");

        public static FrameworkRuleMetadata AutofacReflectionRegistration { get; set; } =
            new("di.autofac.reflection_registration", "di", ExtractionRuleMode.HardEdge, "autofac_reflection_registration");

        public static FrameworkRuleMetadata AutofacModuleLoad { get; set; } =
            new("di.autofac.module_load", "di", ExtractionRuleMode.HardEdge, "autofac_module_load");

        public static FrameworkRuleMetadata ReflectionConstructorDispatch { get; set; } =
            new("reflection.constructor_dispatch", "reflection", ExtractionRuleMode.HardEdge, "reflection_constructor_dispatch");

        public static FrameworkRuleMetadata ReflectionConstructorCandidate { get; set; } =
            new("reflection.constructor_candidate", "reflection", ExtractionRuleMode.Candidate, "reflection_constructor_dispatch");

        public static FrameworkRuleMetadata ServiceProviderDispatch { get; set; } =
            new("di.service_provider_dispatch", "di", ExtractionRuleMode.HardEdge, "service_provider_dispatch");

        public static FrameworkRuleMetadata AutofacResolveDispatch { get; set; } =
            new("di.autofac.resolve_dispatch", "di", ExtractionRuleMode.HardEdge, "autofac_resolve_dispatch");

        public static FrameworkRuleMetadata EventDispatch { get; set; } =
            new("ui.event_dispatch", "ui", ExtractionRuleMode.HardEdge, "event_dispatch");

        public static FrameworkRuleMetadata FrameworkDelegateDispatch { get; set; } =
            new("framework.delegate_dispatch", "framework", ExtractionRuleMode.HardEdge, "framework_delegate_dispatch");

        public static FrameworkRuleMetadata DelegateReference { get; set; } =
            new("framework.delegate_reference", "framework", ExtractionRuleMode.HardEdge, "delegate_reference");

        public static FrameworkRuleMetadata FrameworkDelegateFallbackCandidate { get; set; } =
            new("framework.delegate_fallback_candidate", "framework", ExtractionRuleMode.Candidate, "framework_delegate_fallback");

    }
}
