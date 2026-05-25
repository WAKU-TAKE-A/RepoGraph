using System.Collections.Generic;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslBindingTests
    {
        [Fact]
        public void Binding_EvaluatesFieldsAndLiterals()
        {
            var evaluator = new DslBindingEvaluator();
            var source = new Dictionary<string, object?>
            {
                { "event", "Click" },
                { "nested.field", "Value1" }
            };

            var binds = new Dictionary<string, string>
            {
                { "eventName", "$event" },
                { "nestedVal", "$nested.field" },
                { "literalVal", "FixedText" },
                { "missingVal", "$missing" }
            };

            var result = evaluator.Evaluate(binds, source);

            Assert.Equal("Click", result["eventName"]);
            Assert.Equal("Value1", result["nestedVal"]);
            Assert.Equal("FixedText", result["literalVal"]);
            
            // missingVal should be omitted
            Assert.False(result.ContainsKey("missingVal"));
        }
    }
}
