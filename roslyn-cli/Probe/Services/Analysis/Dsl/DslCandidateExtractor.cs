using System.Collections.Generic;
using Probe.Services.Analysis;

namespace Probe.Services.Analysis.Dsl
{
    public class DslCandidateExtractor
    {
        private readonly DslConditionEvaluator _conditionEvaluator;
        private readonly DslBindingEvaluator _bindingEvaluator;
        private readonly DslTargetResolver _targetResolver;
        private readonly DslCandidateEmitter _emitter;

        public DslCandidateExtractor(
            DslConditionEvaluator conditionEvaluator,
            DslBindingEvaluator bindingEvaluator,
            DslTargetResolver targetResolver,
            DslCandidateEmitter emitter)
        {
            _conditionEvaluator = conditionEvaluator;
            _bindingEvaluator = bindingEvaluator;
            _targetResolver = targetResolver;
            _emitter = emitter;
        }

        public DslExtractionResult Extract(
            IEnumerable<DslRule> rules,
            IEnumerable<IReadOnlyDictionary<string, object?>> sources,
            IEnumerable<IReadOnlyDictionary<string, object?>> targets)
        {
            var result = new DslExtractionResult();

            // Materialise targets once; scope filtering is per-rule inside the loop.
            var targetList = new System.Collections.Generic.List<IReadOnlyDictionary<string, object?>>(targets);

            foreach (var rule in rules)
            {
                if (rule.Emit == null) continue;

                // Determine scope constraints (empty string means no constraint).
                var scopeSource = rule.Scope?.Source ?? string.Empty;
                var scopeTarget = rule.Scope?.Target ?? string.Empty;

                // Filter targets by scope.target once per rule.
                var scopedTargets = string.IsNullOrWhiteSpace(scopeTarget)
                    ? (System.Collections.Generic.IEnumerable<IReadOnlyDictionary<string, object?>>)targetList
                    : targetList.FindAll(t =>
                        t.TryGetValue("source_type", out var st) &&
                        string.Equals(st?.ToString(), scopeTarget, System.StringComparison.Ordinal));

                foreach (var source in sources)
                {
                    // 0. scope.source filter
                    if (!string.IsNullOrWhiteSpace(scopeSource))
                    {
                        if (!source.TryGetValue("source_type", out var sourceType) ||
                            !string.Equals(sourceType?.ToString(), scopeSource, System.StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }

                    // 1. match.where
                    if (rule.Match != null && rule.Match.Where != null)
                    {
                        if (!_conditionEvaluator.Evaluate(rule.Match.Where, source))
                        {
                            continue;
                        }
                    }

                    // 2. bind variables
                    var boundValues = _bindingEvaluator.Evaluate(rule.Bind, source);

                    // 3. resolve target
                    if (rule.Resolve != null && rule.Resolve.Target != null)
                    {
                        var resolvedTargets = _targetResolver.Resolve(rule.Resolve.Target, scopedTargets, boundValues);

                        // 4. emit candidate edge
                        foreach (var target in resolvedTargets)
                        {
                            _emitter.Emit(rule, source, target, result);
                        }
                    }
                }
            }

            return result;
        }
    }
}
