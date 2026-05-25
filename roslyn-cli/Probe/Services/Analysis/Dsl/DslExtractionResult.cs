using System.Collections.Generic;

namespace Probe.Services.Analysis.Dsl
{
    public sealed class DslExtractionResult
    {
        public ExtractionResult Extraction { get; } = new();
        public List<DslDiagnostic> Diagnostics { get; } = new();
    }
}
