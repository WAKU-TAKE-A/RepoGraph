using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Probe.Services.Analysis;
using Probe.Services.Persistence;
using Probe.Services.Graph;
using Probe.Config;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Probe
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var serviceProvider = new ServiceCollection()
                .AddLogging(builder => builder.AddConsole())
                .AddSingleton<WorkspaceLoader>()
                .AddSingleton<SymbolExtractor>()
                .AddSingleton<ConfigLoader>()
                .BuildServiceProvider();

            var rootCommand = new RootCommand("RepoGraph - Roslyn Repository Analyzer");

            var pathArgument = new Argument<string>("path", "Path to .sln or .csproj file");
            var outputOption = new Option<string>("--output", () => "./analysis_workspace", "Output directory");
            var modeOption = new Option<string>("--mode", () => "full", "Scan mode: full or incremental");

            var scanCommand = new Command("scan", "Analyze a C# solution or project")
            {
                pathArgument,
                outputOption,
                modeOption
            };

            scanCommand.SetHandler(async (string path, string output, string mode) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var loader = serviceProvider.GetRequiredService<WorkspaceLoader>();
                var extractor = serviceProvider.GetRequiredService<SymbolExtractor>();
                
                var dbPath = Path.Combine(output, "output", "repository.db");
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? "output");
                
                var persistence = new PersistenceService(dbPath, serviceProvider.GetRequiredService<ILogger<PersistenceService>>());
                await persistence.InitializeAsync();

                if (!string.Equals(mode, "incremental", StringComparison.OrdinalIgnoreCase))
                {
                    await persistence.ResetAnalysisDataAsync();
                }

                var lastRunTime = mode == "incremental" ? await persistence.GetLastRunTimeAsync(path) : null;
                if (lastRunTime.HasValue)
                {
                    logger.LogInformation("Incremental scan enabled. Last run time: {LastRun}", lastRunTime.Value);
                }

                var runId = Guid.NewGuid().ToString();
                await persistence.SaveAnalysisRunAsync(runId, path);

                using var workspace = await loader.LoadWorkspaceAsync(path);
                
                var solutionId = workspace.CurrentSolution.Id.Id.ToString();
                await persistence.SaveSolutionAsync(solutionId, runId, path, Path.GetFileName(path));

                var configLoader = serviceProvider.GetRequiredService<ConfigLoader>();
                var config = configLoader.Load(path);
                var filterService = new FilterService(config, serviceProvider.GetRequiredService<ILogger<FilterService>>());

                var projects = path.EndsWith(".csproj") 
                    ? workspace.CurrentSolution.Projects.Where(p => p.FilePath?.EndsWith(".csproj") == true).ToList()
                    : workspace.CurrentSolution.Projects.ToList();
                logger.LogInformation("Found {Count} projects", projects.Count);
                var savedProjectIds = new HashSet<ProjectId>();
                var projectDependencies = new List<ProjectDependencyData>();

                foreach (var project in projects)
                {
                    try
                    {
                        if (filterService.ShouldExcludeFile(project.FilePath ?? project.Name))
                        {
                            logger.LogInformation("Skipping excluded project: {Name}", project.Name);
                            continue;
                        }

                        logger.LogInformation("Analyzing project: {Name}", project.Name);
                        var projectId = project.Id.Id.ToString();
                        savedProjectIds.Add(project.Id);
                        await persistence.SaveProjectAsync(projectId, solutionId, runId, project.Name, project.FilePath ?? project.Name);

                        var compilation = await project.GetCompilationAsync();
                        if (compilation == null)
                        {
                            logger.LogWarning("Compilation was null for project {Name}", project.Name);
                            continue;
                        }

                        foreach (var document in project.Documents)
                        {
                            try
                            {
                                if (filterService.ShouldExcludeFile(document.FilePath ?? document.Name))
                                {
                                    continue;
                                }

                                if (lastRunTime.HasValue && document.FilePath != null && File.Exists(document.FilePath))
                                {
                                    var lastWriteTime = File.GetLastWriteTimeUtc(document.FilePath);
                                    if (lastWriteTime <= lastRunTime.Value)
                                    {
                                        logger.LogDebug("Skipping unchanged file: {Name}", document.Name);
                                        continue;
                                    }
                                }

                                var documentId = document.Id.Id.ToString();
                                await persistence.SaveDocumentAsync(documentId, projectId, document.FilePath ?? document.Name, document.Name);

                                var tree = await document.GetSyntaxTreeAsync();
                                if (tree == null) continue;

                                var result = extractor.Extract(compilation, tree);

                                foreach (var symbol in result.Symbols)
                                {
                                    symbol.ProjectId = projectId;
                                    symbol.DocumentId = documentId;
                                }

                                if (result.Symbols.Any())
                                {
                                    await persistence.SaveSymbolsAsync(result.Symbols);
                                }

                                if (result.MethodCalls.Any())
                                {
                                    await persistence.SaveMethodCallsAsync(result.MethodCalls);
                                }

                                if (result.Inheritances.Any())
                                {
                                    await persistence.SaveInheritancesAsync(result.Inheritances);
                                }

                                if (result.FieldAccesses.Any())
                                {
                                    await persistence.SaveFieldAccessesAsync(result.FieldAccesses);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to analyze document {Document} in project {Project}", document.Name, project.Name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to analyze project {Project}", project.Name);
                    }
                }

                foreach (var project in projects.Where(p => savedProjectIds.Contains(p.Id)))
                {
                    foreach (var projectReference in project.ProjectReferences)
                    {
                        if (!savedProjectIds.Contains(projectReference.ProjectId))
                        {
                            continue;
                        }

                        projectDependencies.Add(new ProjectDependencyData
                        {
                            SourceProjectId = project.Id.Id.ToString(),
                            TargetProjectId = projectReference.ProjectId.Id.ToString()
                        });
                    }
                }

                if (projectDependencies.Any())
                {
                    await persistence.SaveProjectDependenciesAsync(projectDependencies);
                }

                logger.LogInformation("Generating graphs...");
                var graphOutputDir = Path.Combine(output, "output", "graphs");
                var graphService = new GraphService(dbPath, graphOutputDir, serviceProvider.GetRequiredService<ILogger<GraphService>>());
                await graphService.ExportGraphsAsync(runId);

                await persistence.UpdateAnalysisRunStatusAsync(runId, "completed");
                logger.LogInformation("Analysis completed.");

            }, pathArgument, outputOption, modeOption);

            rootCommand.AddCommand(scanCommand);

            return await rootCommand.InvokeAsync(args);
        }
    }
}
