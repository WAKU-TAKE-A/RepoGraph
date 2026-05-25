using Probe.Config;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class AnalyzerConfigTests
    {
        [Fact]
        public void DefaultAnalysisConfig_DisablesDslCandidates()
        {
            var config = new AnalysisConfig();

            // PROOF: default-off behavior
            Assert.False(config.EnableDslCandidates, "DSL candidates must be disabled by default.");
            Assert.Equal("", config.DslRulesDirectory);
        }
    }
}
