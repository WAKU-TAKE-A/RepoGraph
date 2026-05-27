using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslProductionRuleTests
    {
        [Fact]
        public void ProductionRules_LoadAndValidate()
        {
            var rulesDir = FindRepoPath("rules", "dsl");
            var loader = new DslRuleLoader(
                NullLogger<DslRuleLoader>.Instance,
                new DslRuleValidator());

            var ruleSet = loader.LoadRules(rulesDir);

            Assert.NotEmpty(ruleSet.Rules);
            Assert.Contains(ruleSet.Rules, r => r.Id == "xaml.event_handlers");
            Assert.Contains(ruleSet.Rules, r => r.Id == "di.service_provider_generic");
            Assert.Contains(ruleSet.Rules, r => r.Id == "di.autofac_resolve_generic");
            Assert.All(ruleSet.Rules, rule =>
            {
                Assert.Equal("candidate", rule.Emit.RuleMode);
                Assert.StartsWith("candidate_", rule.Emit.EdgeKind);
            });
        }

        [Fact]
        public void ExternalRuleFile_LoadsWithoutCodeRegistration_AndEmitsCandidate()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "repograph-dsl-external-" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            try
            {
                File.WriteAllText(
                    Path.Combine(tempDir, "custom.lifecycle_rule.json"),
                    """
                    {
                      "id": "custom.lifecycle_probe",
                      "description": "External custom rule used to prove rule files are discovered dynamically.",
                      "scope": { "source": "framework_synthetic", "target": "csharp_symbol" },
                      "match": {
                        "where": [
                          { "field": "source_type", "op": "eq", "value": "framework_synthetic" }
                        ]
                      },
                      "bind": {
                        "target_name": "Run"
                      },
                      "resolve": {
                        "target": {
                          "kind": "method",
                          "where": [
                            { "field": "name", "op": "eq", "value": "$target_name" }
                          ]
                        }
                      },
                      "emit": {
                        "edge_kind": "candidate_call",
                        "call_type": "custom_lifecycle",
                        "rule_id": "custom.lifecycle_probe",
                        "rule_family": "custom",
                        "rule_mode": "candidate",
                        "confidence": "medium"
                      }
                    }
                    """,
                    Encoding.UTF8);

                var loader = new DslRuleLoader(
                    NullLogger<DslRuleLoader>.Instance,
                    new DslRuleValidator());
                var ruleSet = loader.LoadRules(tempDir);

                Assert.Single(ruleSet.Rules);
                Assert.Equal("custom.lifecycle_probe", ruleSet.Rules[0].Id);

                var extractor = new DslCandidateExtractor(
                    new DslConditionEvaluator(),
                    new DslBindingEvaluator(),
                    new DslTargetResolver(new DslConditionEvaluator()),
                    new DslCandidateEmitter());

                var result = extractor.Extract(
                    ruleSet.Rules,
                    new[]
                    {
                        new System.Collections.Generic.Dictionary<string, object?>
                        {
                            ["source_type"] = "framework_synthetic",
                            ["fqn"] = "External.Framework"
                        }
                    },
                    new[]
                    {
                        new System.Collections.Generic.Dictionary<string, object?>
                        {
                            ["source_type"] = "csharp_symbol",
                            ["fqn"] = "App.ExternalEntry.Run()",
                            ["kind"] = "method",
                            ["name"] = "Run"
                        }
                    });

                Assert.Empty(result.Diagnostics);
                var call = Assert.Single(result.Extraction.MethodCalls);
                Assert.Equal("External.Framework", call.CallerId);
                Assert.Equal("App.ExternalEntry.Run()", call.CalleeId);
                Assert.Equal("custom.lifecycle_probe", call.RuleId);
                Assert.Equal("custom", call.RuleFamily);
                Assert.Equal("candidate", call.RuleMode);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        private static string FindRepoPath(params string[] relativeParts)
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                var candidate = Path.Combine(new[] { current.FullName }.Concat(relativeParts).ToArray());
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"Could not find {Path.Combine(relativeParts)} from test output directory.");
        }
    }
}
