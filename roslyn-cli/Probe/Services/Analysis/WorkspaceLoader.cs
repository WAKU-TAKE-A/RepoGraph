using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System;
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
                        var instance = instances.OrderByDescending(x => x.Version).First();
                        _logger.LogInformation("Registering MSBuild instance: {Name} {Version} from {Path}", 
                            instance.Name, instance.Version, instance.MSBuildPath);
                        MSBuildLocator.RegisterInstance(instance);
                    }
                    else
                    {
                        _logger.LogWarning("No MSBuild instances found via MSBuildLocator.");
                    }
                }

                var workspace = MSBuildWorkspace.Create();

                _ = workspace.RegisterWorkspaceFailedHandler(e =>
                {
                    _logger.LogWarning("Workspace diagnostic: {Message}", e.Diagnostic.Message);
                }, null);

                _logger.LogInformation("Loading target: {Path}", targetPath);
                if (targetPath.EndsWith(".sln"))
                {
                    await workspace.OpenSolutionAsync(targetPath);
                }
                else if (targetPath.EndsWith(".csproj"))
                {
                    await workspace.OpenProjectAsync(targetPath);
                }
                else
                {
                    throw new ArgumentException("Unsupported file type. Use .sln or .csproj");
                }
                
                return workspace;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load workspace for {Path}", targetPath);
                throw;
            }
        }
    }
}
