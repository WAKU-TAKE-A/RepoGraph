using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Probe.Services.Analysis.Dsl
{
    public class DslConditionEvaluator
    {
        public bool Evaluate(
            List<DslCondition> conditions, 
            IReadOnlyDictionary<string, object?> sourceData,
            IReadOnlyDictionary<string, object?>? boundValues = null)
        {
            if (conditions == null || conditions.Count == 0) return true;

            foreach (var cond in conditions)
            {
                if (!EvaluateSingle(cond, sourceData, boundValues))
                {
                    return false;
                }
            }
            return true;
        }

        private bool EvaluateSingle(
            DslCondition cond, 
            IReadOnlyDictionary<string, object?> sourceData,
            IReadOnlyDictionary<string, object?>? boundValues)
        {
            bool hasField = sourceData.TryGetValue(cond.Field, out var sourceValue);
            
            if (cond.Op == "exists")
            {
                return hasField;
            }

            if (!hasField)
            {
                return false;
            }

            if (sourceValue == null)
            {
                return cond.Op == "neq";
            }

            var sourceString = sourceValue.ToString() ?? string.Empty;
            var expectedValues = GetExpectedValues(cond.Value);

            var resolvedExpectedValues = new List<string>();
            foreach (var expected in expectedValues)
            {
                if (expected.StartsWith("$"))
                {
                    var varName = expected.Substring(1);
                    if (boundValues != null && boundValues.TryGetValue(varName, out var boundObj) && boundObj != null)
                    {
                        resolvedExpectedValues.Add(boundObj.ToString() ?? string.Empty);
                    }
                    else
                    {
                        return false; 
                    }
                }
                else
                {
                    resolvedExpectedValues.Add(expected);
                }
            }

            if (resolvedExpectedValues.Count == 0)
            {
                return cond.Op == "neq";
            }

            switch (cond.Op)
            {
                case "eq":
                    return resolvedExpectedValues.Any(v => string.Equals(sourceString, v, StringComparison.Ordinal));
                case "neq":
                    return !resolvedExpectedValues.Any(v => string.Equals(sourceString, v, StringComparison.Ordinal));
                case "in":
                    return resolvedExpectedValues.Any(v => string.Equals(sourceString, v, StringComparison.Ordinal));
                case "contains":
                    return resolvedExpectedValues.Any(v => sourceString.Contains(v, StringComparison.Ordinal));
                case "prefix":
                    return resolvedExpectedValues.Any(v => sourceString.StartsWith(v, StringComparison.Ordinal));
                case "suffix":
                    return resolvedExpectedValues.Any(v => sourceString.EndsWith(v, StringComparison.Ordinal));
                case "regex":
                    return resolvedExpectedValues.Any(v => Regex.IsMatch(sourceString, v));
                default:
                    return false;
            }
        }

        private List<string> GetExpectedValues(object? valueObj)
        {
            var result = new List<string>();
            if (valueObj == null) return result;

            if (valueObj is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (s != null) result.Add(s);
                    }
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    var s = element.GetString();
                    if (s != null) result.Add(s);
                }
            }
            else if (valueObj is string s)
            {
                result.Add(s);
            }
            else if (valueObj is IEnumerable<string> e)
            {
                result.AddRange(e);
            }

            return result;
        }
    }
}
