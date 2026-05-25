using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class CoreHeuristicPolicyTests
    {
        private static readonly string[] BannedTerms = new[]
        {
            "Autofac",
            "Newtonsoft",
            "AspNet",
            "AspNetCore",
            "CommunityToolkit",
            "MediatR",
            "GetRequiredService",
            "ConfigureServices",
            "StartupUri",
            "JsonConverter",
            "ContractResolver",
            "RegisterAssemblyModules",
            "HwndSource"
        };

        // Temporary migration debt: legacy files allowed to contain framework-specific logic for now
        private static readonly string[] TemporaryLegacyAllowlist = new[]
        {
            "DelegateReferenceExtractor.cs",
            "FrameworkRuleCatalog.cs",
            "FrameworkRuleCatalogLoader.cs"
        };

        [Fact]
        public void CoreAnalysis_ShouldNotContainPackageSpecificHeuristics()
        {
            var repoRoot = FindRepoRoot();
            Assert.NotNull(repoRoot);

            var analysisDir = Path.Combine(repoRoot, "roslyn-cli", "Probe", "Services", "Analysis");
            Assert.True(Directory.Exists(analysisDir), $"Analysis directory not found: {analysisDir}");

            var csFiles = Directory.GetFiles(analysisDir, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                // Exclude Dsl directory
                if (file.Replace('\\', '/').Contains("/Services/Analysis/Dsl/"))
                {
                    continue;
                }

                var fileName = Path.GetFileName(file);
                if (TemporaryLegacyAllowlist.Contains(fileName))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(repoRoot, file);
                var lines = File.ReadAllLines(file, Encoding.UTF8);

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    foreach (var term in BannedTerms)
                    {
                        if (line.Contains(term))
                        {
                            Assert.Fail($"Policy violation in {relativePath}\nLine {i + 1}\nMatched term: {term}\nText: {line.Trim()}");
                        }
                    }
                }
            }
        }

        private string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var probeProj = Path.Combine(dir.FullName, "roslyn-cli", "Probe", "Probe.csproj");
                if (File.Exists(probeProj))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}
