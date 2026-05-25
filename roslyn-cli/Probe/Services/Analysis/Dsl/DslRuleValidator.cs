using System.Collections.Generic;

namespace Probe.Services.Analysis.Dsl
{
    public class DslValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new();
    }

    public class DslRuleValidator
    {
        private static readonly HashSet<string> SupportedOperators = new()
        {
            "eq", "neq", "in", "contains", "prefix", "suffix", "regex", "exists"
        };

        private static readonly HashSet<string> UnsupportedOperators = new()
        {
            "has_attribute", "has_base_type", "is_assignable_to", "same_containing_type"
        };

        public DslValidationResult Validate(DslRule rule)
        {
            var result = new DslValidationResult();

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                result.Errors.Add("Rule missing ID.");
            }

            if (rule.Scope == null || string.IsNullOrWhiteSpace(rule.Scope.Source))
            {
                result.Errors.Add("Rule scope missing source.");
            }

            if (rule.Match != null && rule.Match.Where != null)
            {
                ValidateConditions(rule.Match.Where, result);
            }

            if (rule.Bind != null)
            {
                foreach (var kvp in rule.Bind)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        result.Errors.Add("Bind key cannot be empty.");
                    }
                    if (string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        result.Errors.Add($"Bind expression for '{kvp.Key}' cannot be empty.");
                    }
                }
            }

            if (rule.Resolve != null && rule.Resolve.Target != null)
            {
                var target = rule.Resolve.Target;
                if (target.Where != null && target.Where.Count > 0)
                {
                    ValidateConditions(target.Where, result);
                }
            }

            if (rule.Emit != null)
            {
                if (string.IsNullOrWhiteSpace(rule.Emit.EdgeKind))
                {
                    result.Errors.Add("Emit edge_kind is missing.");
                }
                else if (rule.Emit.EdgeKind != "candidate_call" && rule.Emit.EdgeKind != "candidate_type_dependency")
                {
                    result.Errors.Add($"Emit edge_kind '{rule.Emit.EdgeKind}' is not supported.");
                }

                if (string.IsNullOrWhiteSpace(rule.Emit.RuleId))
                {
                    result.Errors.Add("Emit rule_id is missing.");
                }

                if (string.IsNullOrWhiteSpace(rule.Emit.RuleFamily))
                {
                    result.Errors.Add("Emit rule_family is missing.");
                }

                if (rule.Emit.RuleMode != "candidate")
                {
                    // The validator may warn or reject if emit.rule_mode is not candidate.
                    // We will reject it to be safe.
                    result.Errors.Add("Emit rule_mode must be 'candidate'.");
                }
            }
            else
            {
                result.Errors.Add("Rule missing emit block.");
            }

            return result;
        }

        private void ValidateConditions(List<DslCondition> conditions, DslValidationResult result)
        {
            foreach (var cond in conditions)
            {
                if (string.IsNullOrWhiteSpace(cond.Field))
                {
                    result.Errors.Add("Condition missing field.");
                }

                if (string.IsNullOrWhiteSpace(cond.Op))
                {
                    result.Errors.Add("Condition missing operator.");
                    continue;
                }

                if (UnsupportedOperators.Contains(cond.Op))
                {
                    result.Errors.Add($"Operator '{cond.Op}' is explicitly unsupported in this checkpoint.");
                }
                else if (!SupportedOperators.Contains(cond.Op))
                {
                    result.Errors.Add($"Operator '{cond.Op}' is not supported.");
                }
            }
        }
    }
}
