using System.Collections.Generic;
using System.Linq;

namespace Probe.Services.Analysis
{
    public static class SymbolExtractionResultNormalizer
    {
        public static void Normalize(ExtractionResult result)
        {
            // Aggregate call counts
            result.MethodCalls = result.MethodCalls
                .GroupBy(c => new { c.CallerId, c.CalleeId, c.CallType, c.RuleId, c.RuleFamily, c.RuleMode })
                .Select(g => new MethodCallData
                {
                    CallerId = g.Key.CallerId,
                    CalleeId = g.Key.CalleeId,
                    CallCount = g.Sum(x => x.CallCount),
                    CallType = g.Key.CallType,
                    RuleId = g.Key.RuleId,
                    RuleFamily = g.Key.RuleFamily,
                    RuleMode = g.Key.RuleMode
                }).ToList();

            // Deduplicate field accesses (upgrade read to read_write if both read and write exist)
            result.FieldAccesses = DeduplicateFieldAccesses(result.FieldAccesses);
            
            result.TypeDependencies = result.TypeDependencies
                .GroupBy(d => new { d.SourceFqn, d.TargetFqn, d.Kind, d.RuleId, d.RuleFamily, d.RuleMode })
                .Select(g => g.First())
                .ToList();
        }

        private static List<FieldAccessData> DeduplicateFieldAccesses(List<FieldAccessData> accesses)
        {
            var grouped = accesses.GroupBy(a => new { a.AccessorFqn, a.TargetFqn, a.IsExternal });
            var deduped = new List<FieldAccessData>();

            foreach (var group in grouped)
            {
                var kinds = group.Select(a => a.AccessKind).Distinct().ToList();
                var mergedKind = (kinds.Contains("read") && kinds.Contains("write")) ? "read_write" : kinds.First();

                deduped.Add(new FieldAccessData
                {
                    AccessorFqn = group.Key.AccessorFqn,
                    TargetFqn = group.Key.TargetFqn,
                    AccessKind = mergedKind,
                    IsExternal = group.Key.IsExternal
                });
            }

            return deduped;
        }
    }
}
