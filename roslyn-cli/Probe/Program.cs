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
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Probe.Services.Analysis.Dsl;

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
                .AddSingleton<XamlRelationshipExtractor>()
                .AddSingleton<ConfigLoader>()
                // DSL rule loading and candidate extraction services (opt-in use only)
                .AddSingleton<DslRuleValidator>()
                .AddSingleton<DslRuleLoader>()
                .AddSingleton<DslConditionEvaluator>()
                .AddSingleton<DslBindingEvaluator>()
                .AddSingleton<DslTargetResolver>()
                .AddSingleton<DslCandidateEmitter>()
                .AddSingleton<DslCandidateExtractor>()
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
                var xamlExtractor = serviceProvider.GetRequiredService<XamlRelationshipExtractor>();
                
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
                
                var solutionId = GetStableId(Path.GetFullPath(path));
                await persistence.SaveSolutionAsync(solutionId, runId, path, Path.GetFileName(path));

                var configLoader = serviceProvider.GetRequiredService<ConfigLoader>();
                var config = configLoader.Load(path);
                var filterService = new FilterService(config, serviceProvider.GetRequiredService<ILogger<FilterService>>());

                var projects = path.EndsWith(".csproj") 
                    ? workspace.CurrentSolution.Projects.Where(p => p.FilePath?.EndsWith(".csproj") == true).ToList()
                    : workspace.CurrentSolution.Projects.ToList();
                logger.LogInformation("Found {Count} projects", projects.Count);
                var includedProjects = projects
                    .Where(p => !filterService.ShouldExcludeFile(p.FilePath ?? p.Name))
                    .ToList();
                var stableProjectIds = new Dictionary<ProjectId, string>();
                var projectDependencies = new List<ProjectDependencyData>();
                var allSymbols = new List<SymbolData>();
                var allInvocationRecords = new List<IReadOnlyDictionary<string, object?>>();
                var allXmlAttributeRecords = new List<IReadOnlyDictionary<string, object?>>();
                var allMethodCalls = new List<MethodCallData>();
                var allInheritances = new List<InheritanceData>();
                var allFieldAccesses = new List<FieldAccessData>();
                var allTypeDependencies = new List<TypeDependencyData>();
                var methodIndex = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var bindingMemberIndex = new Dictionary<string, List<SymbolData>>(StringComparer.Ordinal);

                foreach (var project in includedProjects)
                {
                    var projectKey = Path.GetFullPath(project.FilePath ?? project.Name);
                    stableProjectIds[project.Id] = GetStableId(projectKey);
                }

                foreach (var project in includedProjects)
                {
                    try
                    {
                        logger.LogInformation("Analyzing project: {Name}", project.Name);
                        var projectKey = Path.GetFullPath(project.FilePath ?? project.Name);
                        var projectId = stableProjectIds[project.Id];
                        var documentsToAnalyze = project.Documents
                            .Where(document => !filterService.ShouldExcludeFile(document.FilePath ?? document.Name))
                            .ToList();

                        bool shouldAnalyzeProject = true;
                        if (lastRunTime.HasValue)
                        {
                            shouldAnalyzeProject = false;

                            if (project.FilePath != null && File.Exists(project.FilePath))
                            {
                                var projectLastWriteTime = File.GetLastWriteTimeUtc(project.FilePath);
                                if (projectLastWriteTime > lastRunTime.Value)
                                {
                                    shouldAnalyzeProject = true;
                                }
                            }

                            if (!shouldAnalyzeProject)
                            {
                                foreach (var document in documentsToAnalyze)
                                {
                                    if (document.FilePath == null || !File.Exists(document.FilePath))
                                    {
                                        shouldAnalyzeProject = true;
                                        break;
                                    }

                                    var lastWriteTime = File.GetLastWriteTimeUtc(document.FilePath);
                                    if (lastWriteTime > lastRunTime.Value)
                                    {
                                        shouldAnalyzeProject = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (!shouldAnalyzeProject)
                        {
                            logger.LogInformation("Skipping unchanged project: {Name}", project.Name);
                            continue;
                        }

                        await persistence.ResetProjectAnalysisDataAsync(projectId);
                        var projectMetadata = LoadProjectMetadata(project.FilePath, project, documentsToAnalyze.Count);
                        await persistence.SaveProjectAsync(
                            projectId,
                            solutionId,
                            runId,
                            projectMetadata.Name,
                            project.FilePath ?? project.Name,
                            projectMetadata.AssemblyName,
                            projectMetadata.TargetFramework,
                            projectMetadata.ProjectType,
                            projectMetadata.IsTestProject,
                            projectMetadata.IsSdkStyle,
                            projectMetadata.DocumentCount);

                        var compilation = await project.GetCompilationAsync();
                        if (compilation == null)
                        {
                            logger.LogWarning("Compilation was null for project {Name}", project.Name);
                            continue;
                        }

                        foreach (var document in documentsToAnalyze)
                        {
                            try
                            {
                                var documentKey = Path.GetFullPath(document.FilePath ?? $"{projectKey}:{document.Name}");
                                var documentId = GetStableId(documentKey);
                                await persistence.SaveDocumentAsync(documentId, projectId, document.FilePath ?? document.Name, document.Name);

                                var tree = await document.GetSyntaxTreeAsync();
                                if (tree == null) continue;

                                if (config.Analysis?.EnableDslCandidates == true)
                                {
                                    var semanticModel = compilation.GetSemanticModel(tree);
                                    var invocations = DslInvocationRecordCollector.Collect(semanticModel, await tree.GetRootAsync());
                                    allInvocationRecords.AddRange(invocations);
                                }

                                var result = extractor.Extract(compilation, tree);

                                foreach (var symbol in result.Symbols)
                                {
                                    symbol.ProjectId = projectId;
                                    symbol.DocumentId = documentId;
                                }

                                if (result.Symbols.Any())
                                {
                                    allSymbols.AddRange(result.Symbols);
                                    await persistence.SaveSymbolsAsync(result.Symbols);
                                    IndexMethods(methodIndex, result.Symbols);
                                    IndexBindingMembers(bindingMemberIndex, result.Symbols);
                                }

                                if (result.MethodCalls.Any())
                                {
                                    allMethodCalls.AddRange(result.MethodCalls);
                                }

                                if (result.Inheritances.Any())
                                {
                                    allInheritances.AddRange(result.Inheritances);
                                }

                                if (result.FieldAccesses.Any())
                                {
                                    allFieldAccesses.AddRange(result.FieldAccesses);
                                }

                                if (result.TypeDependencies.Any())
                                {
                                    allTypeDependencies.AddRange(result.TypeDependencies);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to analyze document {Document} in project {Project}", document.Name, project.Name);
                            }
                        }

                        var xamlPaths = FindProjectXamlFiles(project.FilePath ?? project.Name, filterService);
                        if (xamlPaths.Count > 0)
                        {
                            if (config.Analysis?.EnableDslCandidates == true)
                            {
                                var xmlRecords = DslXmlAttributeRecordCollector.Collect(xamlPaths);
                                allXmlAttributeRecords.AddRange(xmlRecords);
                            }

                            var xamlResult = xamlExtractor.Extract(project.Name, projectId, project.FilePath ?? project.Name, xamlPaths, methodIndex, bindingMemberIndex);

                            foreach (var xamlDocument in xamlResult.Documents)
                            {
                                await persistence.SaveDocumentAsync(xamlDocument.Id, xamlDocument.ProjectId, xamlDocument.FilePath, xamlDocument.Name);
                            }

                            if (xamlResult.Symbols.Count > 0)
                            {
                                allSymbols.AddRange(xamlResult.Symbols);
                                await persistence.SaveSymbolsAsync(xamlResult.Symbols);
                            }

                            if (xamlResult.MethodCalls.Count > 0)
                            {
                                allMethodCalls.AddRange(xamlResult.MethodCalls);
                            }

                            if (xamlResult.TypeDependencies.Count > 0)
                            {
                                allTypeDependencies.AddRange(xamlResult.TypeDependencies);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to analyze project {Project}", project.Name);
                    }
                }

                foreach (var project in includedProjects)
                {
                    foreach (var projectReference in project.ProjectReferences)
                    {
                        if (!stableProjectIds.ContainsKey(projectReference.ProjectId))
                        {
                            continue;
                        }

                        projectDependencies.Add(new ProjectDependencyData
                        {
                            SourceProjectId = stableProjectIds[project.Id],
                            TargetProjectId = stableProjectIds[projectReference.ProjectId]
                        });
                    }
                }

                if (projectDependencies.Any())
                {
                    await persistence.SaveProjectDependenciesAsync(projectDependencies);
                }

                // DSL candidate activation (opt-in only; supported source scopes are filtered below).
                if (config.Analysis?.EnableDslCandidates == true)
                {
                    logger.LogInformation("DSL candidate rules enabled.");

                    var dslRulesDir = config.Analysis.DslRulesDirectory;
                    if (string.IsNullOrWhiteSpace(dslRulesDir))
                    {
                        dslRulesDir = Path.Combine(AppContext.BaseDirectory, "rules", "dsl");
                    }

                    logger.LogInformation("DSL rules directory: {Dir}", dslRulesDir);

                    var dslLoader = serviceProvider.GetRequiredService<DslRuleLoader>();
                    var ruleSet = dslLoader.LoadRules(dslRulesDir);
                    logger.LogInformation("Loaded DSL rules: {Count}", ruleSet.Rules.Count);

                    if (ruleSet.Rules.Count > 0)
                    {
                        // Collect all SymbolData extracted so far from persistence symbols list.
                        // We use the allSymbols list that is already available in scope.
                        var symbolRecords = DslSymbolDataAdapter.FromSymbolDataCollection(allSymbols);

                        var allSources = new List<IReadOnlyDictionary<string, object?>>();
                        allSources.AddRange(symbolRecords);
                        allSources.AddRange(allInvocationRecords);
                        allSources.AddRange(allXmlAttributeRecords);

                        // Only run rules scoped to supported sources in this checkpoint.
                        var validScopes = new HashSet<string> { "csharp_symbol", "csharp_invocation", "xml_attribute" };
                        var symbolRules = ruleSet.Rules
                            .Where(r => r.Scope != null && validScopes.Contains(r.Scope.Source ?? string.Empty))
                            .ToList();

                        var dslExtractor = serviceProvider.GetRequiredService<DslCandidateExtractor>();
                        var dslResult = dslExtractor.Extract(symbolRules, allSources, symbolRecords);

                        logger.LogInformation("DSL candidate method calls: {Count}",
                            dslResult.Extraction.MethodCalls.Count);
                        logger.LogInformation("DSL candidate type dependencies: {Count}",
                            dslResult.Extraction.TypeDependencies.Count);
                        logger.LogInformation("DSL diagnostics: {Count}",
                            dslResult.Diagnostics.Count);

                        if (dslResult.Diagnostics.Count > 0)
                        {
                            foreach (var diag in dslResult.Diagnostics.Take(10))
                            {
                                logger.LogWarning("DSL diagnostic [{Rule}] {Code}: {Message}",
                                    diag.RuleId, diag.Code, diag.Message);
                            }
                        }

                        // Merge candidate edges into existing lists before save.
                        allMethodCalls.AddRange(dslResult.Extraction.MethodCalls);
                        allTypeDependencies.AddRange(dslResult.Extraction.TypeDependencies);
                    }
                }

                if (allMethodCalls.Any())
                {
                    await persistence.SaveMethodCallsAsync(allMethodCalls);
                }

                if (allInheritances.Any())
                {
                    await persistence.SaveInheritancesAsync(allInheritances);
                }

                if (allFieldAccesses.Any())
                {
                    await persistence.SaveFieldAccessesAsync(allFieldAccesses);
                }

                if (allTypeDependencies.Any())
                {
                    await persistence.SaveTypeDependenciesAsync(allTypeDependencies);
                }

                await persistence.UpdateMetricsAsync();

                logger.LogInformation("Generating graphs...");
                var graphOutputDir = Path.Combine(output, "output", "graphs");
                var graphService = new GraphService(dbPath, graphOutputDir, serviceProvider.GetRequiredService<ILogger<GraphService>>());
                await graphService.ExportGraphsAsync(runId, mode, path);


                await persistence.UpdateAnalysisRunStatusAsync(runId, "completed");
                logger.LogInformation("Analysis completed.");

            }, pathArgument, outputOption, modeOption);

            rootCommand.AddCommand(scanCommand);

            return await rootCommand.InvokeAsync(args);
        }

        private static string GetStableId(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static void IndexMethods(Dictionary<string, List<string>> methodIndex, IEnumerable<SymbolData> symbols)
        {
            foreach (var symbol in symbols)
            {
                if (symbol.Kind != "method" || string.IsNullOrWhiteSpace(symbol.ContainingType))
                {
                    continue;
                }

                var key = $"{symbol.ContainingType}|{symbol.Name}";
                if (!methodIndex.TryGetValue(key, out var methods))
                {
                    methods = new List<string>();
                    methodIndex[key] = methods;
                }

                if (!methods.Contains(symbol.Fqn, StringComparer.Ordinal))
                {
                    methods.Add(symbol.Fqn);
                }
            }
        }



        private static void IndexBindingMembers(Dictionary<string, List<SymbolData>> memberIndex, IEnumerable<SymbolData> symbols)
        {
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol.ContainingType))
                {
                    continue;
                }

                if (symbol.Kind is not ("method" or "property" or "field"))
                {
                    continue;
                }

                var key = $"{symbol.ContainingType}|{symbol.Name}";
                if (!memberIndex.TryGetValue(key, out var members))
                {
                    members = new List<SymbolData>();
                    memberIndex[key] = members;
                }

                if (!members.Any(existing => string.Equals(existing.Fqn, symbol.Fqn, StringComparison.Ordinal)))
                {
                    members.Add(symbol);
                }
            }
        }

        private static List<string> FindProjectXamlFiles(string projectPath, FilterService filterService)
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            var projectDir = Path.GetDirectoryName(fullProjectPath);
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            {
                return new List<string>();
            }

            var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var xamlPath in EnumerateDeclaredXamlFiles(fullProjectPath))
            {
                if (IsRelevantXamlPath(xamlPath, filterService))
                {
                    discoveredPaths.Add(Path.GetFullPath(xamlPath));
                }
            }

            foreach (var xamlPath in Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    IsRelevantXamlPath(path, filterService)))
            {
                discoveredPaths.Add(Path.GetFullPath(xamlPath));
            }

            return discoveredPaths.ToList();
        }

        private static IEnumerable<string> EnumerateDeclaredXamlFiles(string projectFilePath)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return EnumerateDeclaredXamlFilesRecursive(projectFilePath, visited);
        }

        private static IEnumerable<string> EnumerateDeclaredXamlFilesRecursive(string projectFilePath, HashSet<string> visited)
        {
            var fullProjectFilePath = Path.GetFullPath(projectFilePath);
            if (!visited.Add(fullProjectFilePath) || !File.Exists(fullProjectFilePath))
            {
                yield break;
            }

            XDocument document;
            try
            {
                document = XDocument.Load(fullProjectFilePath);
            }
            catch
            {
                yield break;
            }

            var projectDir = Path.GetDirectoryName(fullProjectFilePath);
            if (string.IsNullOrWhiteSpace(projectDir))
            {
                yield break;
            }

            foreach (var element in document.Descendants())
            {
                var localName = element.Name.LocalName;
                if (localName is "Page" or "ApplicationDefinition" or "Resource")
                {
                    var include = element.Attribute("Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include))
                    {
                        continue;
                    }

                    var resolved = ResolveProjectRelativePath(projectDir, include);
                    if (LooksLikeXamlPath(resolved))
                    {
                        yield return resolved;
                    }
                }

                if (localName.Equals("Import", StringComparison.OrdinalIgnoreCase))
                {
                    var importedProject = element.Attribute("Project")?.Value;
                    if (string.IsNullOrWhiteSpace(importedProject))
                    {
                        continue;
                    }

                    var importedPath = ResolveProjectRelativePath(projectDir, importedProject);
                    foreach (var importedXaml in EnumerateDeclaredXamlFilesRecursive(importedPath, visited))
                    {
                        yield return importedXaml;
                    }
                }
            }
        }

        private static string ResolveProjectRelativePath(string baseDirectory, string relativeOrAbsolutePath)
        {
            return Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
                ? relativeOrAbsolutePath
                : Path.Combine(baseDirectory, relativeOrAbsolutePath));
        }

        private static bool LooksLikeXamlPath(string path)
        {
            return path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRelevantXamlPath(string path, FilterService filterService)
        {
            return LooksLikeXamlPath(path)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !filterService.ShouldExcludeFile(path);
        }

        private static ProjectMetadata LoadProjectMetadata(string? projectFilePath, Project project, int documentCount)
        {
            var displayName = Path.GetFileNameWithoutExtension(projectFilePath)
                ?? project.AssemblyName
                ?? project.Name;
            var assemblyName = project.AssemblyName;
            var targetFramework = (string?)null;
            var projectType = project.CompilationOptions?.OutputKind.ToString();
            var isTestProject = false;
            var isSdkStyle = false;

            if (!string.IsNullOrWhiteSpace(projectFilePath) && File.Exists(projectFilePath))
            {
                try
                {
                    var root = XDocument.Load(projectFilePath).Root;
                    if (root != null)
                    {
                        displayName = FirstElementValue(root, "AssemblyName")
                            ?? displayName;
                        assemblyName ??= FirstElementValue(root, "AssemblyName");
                        targetFramework = FirstElementValue(root, "TargetFramework")
                            ?? FirstElementValue(root, "TargetFrameworks");
                        projectType = FirstElementValue(root, "OutputType")
                            ?? projectType;
                        var explicitIsTestProject = FirstElementValue(root, "IsTestProject");
                        isSdkStyle = root.Attribute("Sdk") != null
                            || root.Elements().Any(element => element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase));
                        isTestProject = InferTestProject(project, projectFilePath, explicitIsTestProject, displayName);
                    }
                }
                catch
                {
                    isTestProject = InferTestProject(project, projectFilePath, null, displayName);
                }
            }
            else
            {
                isTestProject = InferTestProject(project, projectFilePath, null, displayName);
            }

            if (string.IsNullOrWhiteSpace(projectType))
            {
                projectType = project.CompilationOptions?.OutputKind == OutputKind.ConsoleApplication
                    ? "Exe"
                    : "Library";
            }

            return new ProjectMetadata
            {
                Name = displayName,
                AssemblyName = assemblyName,
                TargetFramework = targetFramework,
                ProjectType = projectType,
                IsTestProject = isTestProject,
                IsSdkStyle = isSdkStyle,
                DocumentCount = documentCount
            };
        }

        private static bool InferTestProject(Project project, string? projectFilePath, string? explicitIsTestProject, string displayName)
        {
            if (bool.TryParse(explicitIsTestProject, out var explicitValue))
            {
                return explicitValue;
            }

            var combinedName = $"{displayName} {project.Name} {project.AssemblyName} {projectFilePath}".ToLowerInvariant();
            if (combinedName.Contains(".test")
                || combinedName.Contains("tests")
                || combinedName.Contains("integrationtest")
                || combinedName.Contains("unittest"))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(projectFilePath) && File.Exists(projectFilePath))
            {
                try
                {
                    var contents = File.ReadAllText(projectFilePath);
                    var testMarkers = new[]
                    {
                        "Microsoft.NET.Test.Sdk",
                        "MSTest",
                        "NUnit",
                        "xunit",
                        "coverlet.msbuild"
                    };
                    return testMarkers.Any(marker => contents.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static string? FirstElementValue(XElement root, string localName)
        {
            return root
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
        }

        private sealed class ProjectMetadata
        {
            public string Name { get; init; } = "";
            public string? AssemblyName { get; init; }
            public string? TargetFramework { get; init; }
            public string? ProjectType { get; init; }
            public bool IsTestProject { get; init; }
            public bool IsSdkStyle { get; init; }
            public int DocumentCount { get; init; }
        }
    }
}
