using System.Collections.Generic;
using System.Linq;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslTargetResolverTests
    {
        [Fact]
        public void TargetResolver_ResolvesWithBoundValuesAndFiltersByKind()
        {
            var conditionEvaluator = new DslConditionEvaluator();
            var targetResolver = new DslTargetResolver(conditionEvaluator);

            var boundValues = new Dictionary<string, object?>
            {
                { "handler_name", "OnClick" }
            };

            var targetSpec = new DslResolveTarget
            {
                Kind = "method",
                Where = new List<DslCondition>
                {
                    new DslCondition { Field = "name", Op = "eq", Value = "$handler_name" }
                }
            };

            var targetRecords = new List<Dictionary<string, object?>>
            {
                new() { { "kind", "method" }, { "name", "OnClick" }, { "id", 1 } },
                new() { { "kind", "method" }, { "name", "OnLoad" }, { "id", 2 } },
                new() { { "kind", "property" }, { "name", "OnClick" }, { "id", 3 } } // wrong kind
            };

            var results = targetResolver.Resolve(targetSpec, targetRecords, boundValues).ToList();

            Assert.Single(results);
            Assert.Equal(1, results[0]["id"]);
        }

        [Fact]
        public void TargetResolver_FailsWhenBoundVariableMissing()
        {
            var conditionEvaluator = new DslConditionEvaluator();
            var targetResolver = new DslTargetResolver(conditionEvaluator);

            var boundValues = new Dictionary<string, object?>(); // Empty bound values

            var targetSpec = new DslResolveTarget
            {
                Kind = "method",
                Where = new List<DslCondition>
                {
                    new DslCondition { Field = "name", Op = "eq", Value = "$missing_var" }
                }
            };

            var targetRecords = new List<Dictionary<string, object?>>
            {
                new() { { "kind", "method" }, { "name", "OnClick" } }
            };

            var results = targetResolver.Resolve(targetSpec, targetRecords, boundValues).ToList();

            Assert.Empty(results);
        }
    }
}
