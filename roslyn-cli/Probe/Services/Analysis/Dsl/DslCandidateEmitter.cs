using System.Collections.Generic;
using Probe.Services.Analysis;

namespace Probe.Services.Analysis.Dsl
{
    public class DslCandidateEmitter
    {
        public void Emit(
            DslRule rule,
            IReadOnlyDictionary<string, object?> sourceData,
            IReadOnlyDictionary<string, object?> targetData,
            DslExtractionResult result)
        {
            if (rule.Emit == null) return;

            var ruleId = rule.Id ?? string.Empty;

            sourceData.TryGetValue("fqn", out var sourceFqnObj);
            var sourceFqn = sourceFqnObj?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceFqn))
            {
                result.Diagnostics.Add(new DslDiagnostic(
                    ruleId,
                    "warning",
                    "missing_source_fqn",
                    "Source record is missing required 'fqn' field."
                ));
                return;
            }

            targetData.TryGetValue("fqn", out var targetFqnObj);
            var targetFqn = targetFqnObj?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetFqn))
            {
                result.Diagnostics.Add(new DslDiagnostic(
                    ruleId,
                    "warning",
                    "missing_target_fqn",
                    "Target record is missing required 'fqn' field."
                ));
                return;
            }

            if (rule.Emit.EdgeKind == "candidate_call")
            {
                result.Extraction.MethodCalls.Add(new MethodCallData
                {
                    CallerId = sourceFqn,
                    CalleeId = targetFqn,
                    CallCount = 1,
                    CallType = string.IsNullOrWhiteSpace(rule.Emit.CallType) ? "calls" : rule.Emit.CallType,
                    RuleId = rule.Emit.RuleId,
                    RuleFamily = rule.Emit.RuleFamily,
                    RuleMode = "candidate" // ENFORCED
                });
            }
            else if (rule.Emit.EdgeKind == "candidate_type_dependency")
            {
                result.Extraction.TypeDependencies.Add(new TypeDependencyData
                {
                    SourceFqn = sourceFqn,
                    TargetFqn = targetFqn,
                    Kind = string.IsNullOrWhiteSpace(rule.Emit.CallType) ? rule.Emit.EdgeKind : rule.Emit.CallType,
                    RuleId = rule.Emit.RuleId,
                    RuleFamily = rule.Emit.RuleFamily,
                    RuleMode = "candidate" // ENFORCED
                });
            }
        }
    }
}
