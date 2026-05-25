using System.Collections.Generic;
using System.Linq;

namespace Probe.Services.Analysis.Dsl
{
    public class DslTargetResolver
    {
        private readonly DslConditionEvaluator _evaluator;

        public DslTargetResolver(DslConditionEvaluator evaluator)
        {
            _evaluator = evaluator;
        }

        public IEnumerable<IReadOnlyDictionary<string, object?>> Resolve(
            DslResolveTarget targetSpec,
            IEnumerable<IReadOnlyDictionary<string, object?>> targetRecords,
            IReadOnlyDictionary<string, object?> boundValues)
        {
            if (targetSpec == null) return Enumerable.Empty<IReadOnlyDictionary<string, object?>>();

            var results = new List<IReadOnlyDictionary<string, object?>>();

            foreach (var record in targetRecords)
            {
                // Filter by target.kind
                if (!string.IsNullOrWhiteSpace(targetSpec.Kind))
                {
                    if (!record.TryGetValue("kind", out var kindValue) || 
                        kindValue?.ToString() != targetSpec.Kind)
                    {
                        continue;
                    }
                }

                if (_evaluator.Evaluate(targetSpec.Where, record, boundValues))
                {
                    results.Add(record);
                }
            }

            return results;
        }
    }
}
