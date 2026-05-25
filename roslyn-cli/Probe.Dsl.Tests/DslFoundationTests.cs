using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslFoundationTests
    {
        [Fact]
        public void Validator_ValidatesRequiredFields()
        {
            var validator = new DslRuleValidator();
            var rule = new DslRule(); // empty

            var result = validator.Validate(rule);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("missing ID"));
            Assert.Contains(result.Errors, e => e.Contains("missing source"));

            rule.Id = "test_rule";
            rule.Scope.Source = "csharp_symbol";
            rule.Emit = new DslEmit
            {
                EdgeKind = "candidate_call",
                RuleMode = "candidate",
                RuleId = "test_rule",
                RuleFamily = "test"
            };
            result = validator.Validate(rule);
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_RejectsUnsupportedOperators()
        {
            var validator = new DslRuleValidator();
            var rule = new DslRule
            {
                Id = "test",
                Scope = new DslScope { Source = "test" },
                Match = new DslMatch
                {
                    Where = new List<DslCondition>
                    {
                        new DslCondition { Field = "name", Op = "has_attribute", Value = "Test" }
                    }
                },
                Emit = new DslEmit
                {
                    EdgeKind = "candidate_call",
                    RuleMode = "candidate",
                    RuleId = "test",
                    RuleFamily = "test"
                }
            };

            var result = validator.Validate(rule);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("explicitly unsupported"));
        }

        [Fact]
        public void Loader_ReadsJsonAsUtf8()
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                // Create a JSON with some non-ASCII characters to test UTF8 loading
                var json = @"
                {
                    ""id"": ""test_utf8"",
                    ""description"": ""日本語の説明"",
                    ""scope"": { ""source"": ""xml_attribute"" },
                    ""match"": {
                        ""where"": [
                            { ""field"": ""name"", ""op"": ""eq"", ""value"": ""Click"" }
                        ]
                    },
                    ""emit"": {
                        ""edge_kind"": ""candidate_call"",
                        ""rule_mode"": ""candidate"",
                        ""rule_id"": ""test_utf8"",
                        ""rule_family"": ""test""
                    }
                }";
                File.WriteAllText(Path.Combine(dir, "test.json"), json, System.Text.Encoding.UTF8);

                var validator = new DslRuleValidator();
                var loader = new DslRuleLoader(NullLogger<DslRuleLoader>.Instance, validator);
                
                var ruleSet = loader.LoadRules(dir);
                
                Assert.Single(ruleSet.Rules);
                var rule = ruleSet.Rules[0];
                Assert.Equal("test_utf8", rule.Id);
                Assert.Equal("日本語の説明", rule.Description);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Evaluator_Exists_ReturnsTrueEvenIfValueIsNull()
        {
            var evaluator = new DslConditionEvaluator();
            var source = new Dictionary<string, object?> { { "name", null } };

            var conditions = new List<DslCondition>
            {
                new DslCondition { Field = "name", Op = "exists" }
            };

            Assert.True(evaluator.Evaluate(conditions, source));
        }

        [Fact]
        public void Evaluator_Exists_ReturnsFalseIfKeyMissing()
        {
            var evaluator = new DslConditionEvaluator();
            var source = new Dictionary<string, object?>(); // empty

            var conditions = new List<DslCondition>
            {
                new DslCondition { Field = "name", Op = "exists" }
            };

            Assert.False(evaluator.Evaluate(conditions, source));
        }

        [Fact]
        public void Evaluator_Eq_ReturnsFalseIfKeyMissing()
        {
            var evaluator = new DslConditionEvaluator();
            var source = new Dictionary<string, object?>();

            var conditions = new List<DslCondition>
            {
                new DslCondition { Field = "name", Op = "eq", Value = "Click" }
            };

            Assert.False(evaluator.Evaluate(conditions, source));
        }

        [Fact]
        public void Evaluator_Eq_ReturnsFalseIfValueIsNull()
        {
            var evaluator = new DslConditionEvaluator();
            var source = new Dictionary<string, object?> { { "name", null } };

            var conditions = new List<DslCondition>
            {
                new DslCondition { Field = "name", Op = "eq", Value = "Click" }
            };

            Assert.False(evaluator.Evaluate(conditions, source));
        }

        [Fact]
        public void Evaluator_Neq_ReturnsFalseIfKeyMissing()
        {
            var evaluator = new DslConditionEvaluator();
            var source = new Dictionary<string, object?>();

            var conditions = new List<DslCondition>
            {
                new DslCondition { Field = "name", Op = "neq", Value = "Click" }
            };

            Assert.False(evaluator.Evaluate(conditions, source));
        }

        [Fact]
        public void Evaluator_Neq_ReturnsTrueIfFieldExistsButDoesNotMatch()
        {
            var evaluator = new DslConditionEvaluator();
            var source = new Dictionary<string, object?> { { "name", "Loaded" } };

            var conditions = new List<DslCondition>
            {
                new DslCondition { Field = "name", Op = "neq", Value = "Click" }
            };

            Assert.True(evaluator.Evaluate(conditions, source));
            
            source["name"] = null;
            Assert.True(evaluator.Evaluate(conditions, source));
        }
    }
}
