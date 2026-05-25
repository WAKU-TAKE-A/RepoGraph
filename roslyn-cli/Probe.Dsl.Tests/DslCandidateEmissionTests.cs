using System.Collections.Generic;
using System.Linq;
using Probe.Services.Analysis;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslCandidateEmissionTests
    {
        private DslCandidateExtractor CreateExtractor()
        {
            var evaluator = new DslConditionEvaluator();
            var binder = new DslBindingEvaluator();
            var resolver = new DslTargetResolver(evaluator);
            var emitter = new DslCandidateEmitter();
            return new DslCandidateExtractor(evaluator, binder, resolver, emitter);
        }

        [Fact]
        public void Extractor_EmitsCandidateCall()
        {
            var extractor = CreateExtractor();
            
            var rule = new DslRule
            {
                Id = "test_rule",
                Match = new DslMatch
                {
                    Where = new List<DslCondition>
                    {
                        new DslCondition { Field = "event", Op = "eq", Value = "Click" }
                    }
                },
                Bind = new Dictionary<string, string>
                {
                    { "handler", "$handler_name" }
                },
                Resolve = new DslResolve
                {
                    Target = new DslResolveTarget
                    {
                        Kind = "method",
                        Where = new List<DslCondition>
                        {
                            new DslCondition { Field = "name", Op = "eq", Value = "$handler" }
                        }
                    }
                },
                Emit = new DslEmit
                {
                    EdgeKind = "candidate_call",
                    CallType = "xaml_event",
                    RuleId = "test_rule",
                    RuleFamily = "test",
                    RuleMode = "hardedge" // JSON might say this, but should be forced to candidate
                }
            };

            var sources = new List<Dictionary<string, object?>>
            {
                new() { { "fqn", "File.xaml:Button" }, { "event", "Click" }, { "handler_name", "OnClick" } }
            };

            var targets = new List<Dictionary<string, object?>>
            {
                new() { { "fqn", "CodeBehind.cs:OnClick" }, { "kind", "method" }, { "name", "OnClick" } },
                new() { { "fqn", "CodeBehind.cs:OtherMethod" }, { "kind", "method" }, { "name", "Other" } }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Single(result.Extraction.MethodCalls);
            var call = result.Extraction.MethodCalls.First();
            Assert.Equal("File.xaml:Button", call.CallerId);
            Assert.Equal("CodeBehind.cs:OnClick", call.CalleeId);
            Assert.Equal("xaml_event", call.CallType);
            Assert.Equal("test_rule", call.RuleId);
            Assert.Equal("candidate", call.RuleMode); // ENFORCED
            Assert.Empty(result.Extraction.TypeDependencies);
        }

        [Fact]
        public void Extractor_EmitsCandidateTypeDependency()
        {
            var extractor = CreateExtractor();
            
            var rule = new DslRule
            {
                Id = "test_type_dep",
                Match = new DslMatch
                {
                    Where = new List<DslCondition>() // match all
                },
                Resolve = new DslResolve
                {
                    Target = new DslResolveTarget
                    {
                        Kind = "class",
                        Where = new List<DslCondition>
                        {
                            new DslCondition { Field = "name", Op = "eq", Value = "TargetClass" }
                        }
                    }
                },
                Emit = new DslEmit
                {
                    EdgeKind = "candidate_type_dependency",
                    CallType = "di_injection",
                    RuleId = "test_type_dep"
                }
            };

            var sources = new List<Dictionary<string, object?>>
            {
                new() { { "fqn", "SourceClass" } }
            };

            var targets = new List<Dictionary<string, object?>>
            {
                new() { { "fqn", "TargetClassFqn" }, { "kind", "class" }, { "name", "TargetClass" } }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Single(result.Extraction.TypeDependencies);
            var dep = result.Extraction.TypeDependencies.First();
            Assert.Equal("SourceClass", dep.SourceFqn);
            Assert.Equal("TargetClassFqn", dep.TargetFqn);
            Assert.Equal("di_injection", dep.Kind);
            Assert.Equal("test_type_dep", dep.RuleId);
            Assert.Equal("candidate", dep.RuleMode);
            Assert.Empty(result.Extraction.MethodCalls);
        }

        [Fact]
        public void Extractor_SkipsEmissionIfFqnMissing()
        {
            var extractor = CreateExtractor();
            
            var rule = new DslRule
            {
                Id = "test_skip",
                Match = new DslMatch { Where = new List<DslCondition>() },
                Resolve = new DslResolve
                {
                    Target = new DslResolveTarget
                    {
                        Kind = "method",
                        Where = new List<DslCondition>()
                    }
                },
                Emit = new DslEmit { EdgeKind = "candidate_call" }
            };

            var sources = new List<Dictionary<string, object?>>
            {
                new() { { "name", "NoFqn" } }
            };

            var targets = new List<Dictionary<string, object?>>
            {
                new() { { "fqn", "TargetFqn" }, { "kind", "method" } }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);
            Assert.Empty(result.Extraction.MethodCalls);
            Assert.Contains(result.Diagnostics, d => d.Code == "missing_source_fqn");
            
            var sources2 = new List<Dictionary<string, object?>>
            {
                new() { { "fqn", "SourceFqn" } }
            };

            var targets2 = new List<Dictionary<string, object?>>
            {
                new() { { "name", "NoFqn" }, { "kind", "method" } }
            };
            
            var result2 = extractor.Extract(new[] { rule }, sources2, targets2);
            Assert.Empty(result2.Extraction.MethodCalls);
            Assert.Contains(result2.Diagnostics, d => d.Code == "missing_target_fqn");
        }
    }
}
