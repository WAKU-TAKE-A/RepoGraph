using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Probe.Services.Analysis;
using Probe.Services.Persistence;
using System.Linq;

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
                .BuildServiceProvider();

            var rootCommand = new RootCommand("RepoGraph - Roslyn Repository Analyzer");

            var scanCommand = new Command("scan", "Analyze a C# solution or project")
            {
                new Argument<string>("path", "Path to .sln or .csproj file"),
                new Option<string>("--output", () => "./analysis_workspace", "Output directory")
            };

            scanCommand.SetHandler(async (string path, string output) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var loader = serviceProvider.GetRequiredService<WorkspaceLoader>();
                var extractor = serviceProvider.GetRequiredService<SymbolExtractor>();
                
                var dbPath = Path.Combine(output, "output", "repository.db");
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? "output");
                
                var persistence = new PersistenceService(dbPath, serviceProvider.GetRequiredService<ILogger<PersistenceService>>());
                await persistence.InitializeAsync();

                var runId = Guid.NewGuid().ToString();
                await persistence.SaveAnalysisRunAsync(runId, path);

                using var workspace = await loader.LoadWorkspaceAsync(path);
                
                var solutionId = workspace.CurrentSolution.Id.Id.ToString();
                await persistence.SaveSolutionAsync(solutionId, runId, path, Path.GetFileName(path));

                var projects = path.EndsWith(".csproj") 
                    ? workspace.CurrentSolution.Projects.Where(p => p.FilePath?.EndsWith(".csproj") == true).ToList()
                    : workspace.CurrentSolution.Projects.ToList();
                logger.LogInformation("Found {Count} projects", projects.Count);

                foreach (var project in projects)
                {
                    logger.LogInformation("Analyzing project: {Name}", project.Name);
                    var projectId = project.Id.Id.ToString();
                    await persistence.SaveProjectAsync(projectId, solutionId, runId, project.Name, project.FilePath ?? project.Name);

                    var compilation = await project.GetCompilationAsync();
                    if (compilation == null) continue;

                    foreach (var document in project.Documents)
                    {
                        var documentId = document.Id.Id.ToString();
                        await persistence.SaveDocumentAsync(documentId, projectId, document.FilePath ?? document.Name, document.Name);

                        var tree = await document.GetSyntaxTreeAsync();
                        if (tree == null) continue;

                        var symbols = extractor.ExtractSymbols(compilation, tree);
                        foreach (var symbol in symbols)
                        {
                            symbol.ProjectId = projectId;
                            symbol.DocumentId = documentId;
                            await persistence.SaveSymbolAsync(symbol);
                        }
                    }
                }

                logger.LogInformation("Analysis completed.");

            }, scanCommand.Arguments[0] as Argument<string>, scanCommand.Options[0] as Option<string>);

            rootCommand.AddCommand(scanCommand);

            return await rootCommand.InvokeAsync(args);
        }
    }
}
