using System.IO;
using System.Linq;
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
