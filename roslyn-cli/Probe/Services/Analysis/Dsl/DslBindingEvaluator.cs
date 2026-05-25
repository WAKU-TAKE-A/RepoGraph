using System.Collections.Generic;

namespace Probe.Services.Analysis.Dsl
{
    public class DslBindingEvaluator
    {
        public IReadOnlyDictionary<string, object?> Evaluate(
            Dictionary<string, string> bindExpressions, 
            IReadOnlyDictionary<string, object?> sourceData)
        {
            var boundValues = new Dictionary<string, object?>();

            if (bindExpressions == null) return boundValues;

            foreach (var kvp in bindExpressions)
            {
                var key = kvp.Key;
                var expression = kvp.Value;

                if (expression.StartsWith("$"))
                {
                    var sourceField = expression.Substring(1);
                    if (sourceData.TryGetValue(sourceField, out var value))
                    {
                        boundValues[key] = value;
                    }
                    // Behavior: Missing source field -> binding is omitted entirely.
                }
                else
                {
                    // Literal
                    boundValues[key] = expression;
                }
            }

            return boundValues;
        }
    }
}
