using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Probe.Services.Analysis
{
    public class WorkspaceLoader
    {
        private readonly ILogger<WorkspaceLoader> _logger;

        public WorkspaceLoader(ILogger<WorkspaceLoader> logger)
        {
            _logger = logger;
        }

        public async Task<MSBuildWorkspace> LoadWorkspaceAsync(string targetPath)
        {
            try
            {
                targetPath = ResolveTargetPath(targetPath);

                // Ensure MSBuild is located
                if (!MSBuildLocator.IsRegistered)
                {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
                    if (instances.Any())
                    {
                        var instance = instances
                            .OrderByDescending(x => IsSdkInstance(x.Name) ? 0 : 1)
                            .ThenByDescending(x => x.Version)
                            .First();
                        _logger.LogInformation("Registering MSBuild instance: {Name} {Version} from {Path}", 
                            instance.Name, instance.Version, instance.MSBuildPath);
                        MSBuildLocator.RegisterInstance(instance);
                    }
                    else
                    {
                        _logger.LogWarning("No MSBuild instances found via MSBuildLocator.");
                    }
                }

                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DesignTimeBuild"] = "true",
                    ["BuildingInsideVisualStudio"] = "true",
                    ["BuildProjectReferences"] = "false",
                    ["SkipCompilerExecution"] = "true",
                    ["ProvideCommandLineArgs"] = "true",
                    ["AlwaysCompileMarkupFilesInSeparateDomain"] = "false",
                    ["UseVSHostingProcess"] = "false",
                    ["RunAnalyzers"] = "false",
                    ["CodeAnalysisRuleSet"] = "",
                    ["TreatWarningsAsErrors"] = "false"
                };

                var workspace = MSBuildWorkspace.Create(properties);
                workspace.LoadMetadataForReferencedProjects = true;

                _ = workspace.RegisterWorkspaceFailedHandler(e =>
                {
                    _logger.LogWarning("Workspace diagnostic: {Message}", e.Diagnostic.Message);
                }, null);

                _logger.LogInformation("Loading target: {Path}", targetPath);
                if (targetPath.EndsWith(".sln") || targetPath.EndsWith(".slnx"))
                {
                    await workspace.OpenSolutionAsync(targetPath);
                }
                else if (targetPath.EndsWith(".csproj"))
                {
                    await workspace.OpenProjectAsync(targetPath);
                }
                else
                {
                    throw new ArgumentException("Unsupported file type. Use .sln, .slnx or .csproj");
                }
                
                return workspace;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load workspace for {Path}", targetPath);
                throw;
            }
        }

        private static bool IsSdkInstance(string? name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                   name.Contains(".NET Core SDK", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveTargetPath(string targetPath)
        {
            if (!Directory.Exists(targetPath))
            {
                return targetPath;
            }

            var fullPath = Path.GetFullPath(targetPath);
            var solutionCandidates = Directory
                .EnumerateFiles(fullPath, "*.sln", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(fullPath, "*.slnx", SearchOption.AllDirectories))
                .Where(path => !IsExcludedDirectory(path))
                .OrderBy(path => SolutionCandidateScore(fullPath, path))
                .ThenBy(path => path.Length)
                .ToList();

            if (solutionCandidates.Count > 0)
            {
                var selected = solutionCandidates[0];
                _logger.LogInformation("Resolved directory {Directory} to solution {Solution}", targetPath, selected);
                return selected;
            }

            var projectCandidates = Directory
                .EnumerateFiles(fullPath, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !IsExcludedDirectory(path))
                .OrderBy(path => ProjectCandidateScore(fullPath, path))
                .ThenBy(path => path.Length)
                .ToList();

            if (projectCandidates.Count > 0)
            {
                var selected = projectCandidates[0];
                _logger.LogInformation("Resolved directory {Directory} to project {Project}", targetPath, selected);
                return selected;
            }

            throw new ArgumentException("Directory does not contain a supported .sln, .slnx, or .csproj file");
        }

        private static bool IsExcludedDirectory(string path)
        {
            var normalized = path.Replace('/', '\\');
            return normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase);
        }

        private static int SolutionCandidateScore(string rootDirectory, string path)
        {
            var relative = Path.GetRelativePath(rootDirectory, path).Replace('/', '\\');
            var score = 0;
            score += relative.Count(ch => ch == '\\') * 10;
            if (relative.StartsWith("src\\", StringComparison.OrdinalIgnoreCase))
            {
                score -= 20;
            }
            if (relative.Contains("\\extension\\", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
            if (relative.Contains("\\demo\\", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }
            return score;
        }

        private static int ProjectCandidateScore(string rootDirectory, string path)
        {
            var relative = Path.GetRelativePath(rootDirectory, path).Replace('/', '\\');
            var score = 0;
            score += relative.Count(ch => ch == '\\') * 10;
            if (relative.StartsWith("src\\", StringComparison.OrdinalIgnoreCase))
            {
                score -= 10;
            }
            if (relative.Contains("demo", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            if (relative.Contains("\\extension\\", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
            return score;
        }
    }
}
