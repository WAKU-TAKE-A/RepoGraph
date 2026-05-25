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



        public static FrameworkRuleMetadata ReflectionConstructorDispatch { get; set; } =
            new("reflection.constructor_dispatch", "reflection", ExtractionRuleMode.HardEdge, "reflection_constructor_dispatch");

        public static FrameworkRuleMetadata ReflectionConstructorCandidate { get; set; } =
            new("reflection.constructor_candidate", "reflection", ExtractionRuleMode.Candidate, "reflection_constructor_dispatch");



    }
}
