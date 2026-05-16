using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
    }
}
