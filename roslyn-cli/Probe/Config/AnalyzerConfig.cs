using System;
using System.Collections.Generic;

namespace Probe.Config
{
    public class AnalyzerConfig
    {
        public ExcludeConfig Exclude { get; set; } = new();
        public AnalysisConfig Analysis { get; set; } = new();
        public PerformanceConfig Performance { get; set; } = new();
    }

    public class ExcludeConfig
    {
        public List<string> Directories { get; set; } = new();
        public List<string> FilePatterns { get; set; } = new();
        public List<string> Paths { get; set; } = new();
        public List<string> Namespaces { get; set; } = new();
    }

    public class AnalysisConfig
    {
        public bool IncludeCallGraph { get; set; } = true;
        public bool IncludeInheritance { get; set; } = true;
        public bool IncludeInterfaceMappings { get; set; } = true;
        public bool IncludeMethodBodies { get; set; } = true;
        // DSL heuristic candidate rules (opt-in, default off)
        public bool EnableDslCandidates { get; set; } = false;
        public string DslRulesDirectory { get; set; } = "";
    }

    public class PerformanceConfig
    {
        public int Parallelism { get; set; } = 4;
        public int MaxDocumentSizeMB { get; set; } = 8;
    }
}
