# RepoGraph: Instructions for AI Agent

## 1. Role

You are using RepoGraph as a map for exploring a large C# / .NET repository.

RepoGraph is not a verdict engine.
RepoGraph is a navigation aid that helps you decide what to read next.

Treat these as primary:

- `files`
- `symbols`
- `show-hotspots`
- graph JSON
- `related`

Treat these as secondary:

- `show-isolation`
- `xaml-candidates`
- `ai-candidates`
- `show-ai-edges`

## 2. Expected Layout

Assume a normal distribution layout like this:

```text
RepoGraph/
  probe/
    Probe.exe
    Probe.dll
    Probe.runtimeconfig.json
    Probe.deps.json
    rules/
    BuildHost-net472/
    BuildHost-netcore/
    runtimes/
    *.dll

  python-rag/
    main.py
    *.py

  analysis_workspace/
  README.md
  AI_INSTRUCTIONS.md
```

Important:

- Do not assume `Probe.exe` alone is sufficient.
- Treat `probe/` as the normal distribution layout. `roslyn-cli/Probe` is a source-tree location for development, not the normal runtime location.
- Do not assume RepoGraph source code is being developed in place.
- If `probe/` or `python-rag/` is missing, treat the distribution as incomplete.
- Do not switch to `dotnet run --project ...` in normal use. That belongs to development mode only.

## 3. Basic Behavior

- Do not start with raw `grep`.
- Do not manually read the whole repository first.
- Use RepoGraph outputs to choose where to read.
- If an `analysis_workspace` for the target already exists, start from that workspace before deciding to rescan.
- If the workspace does not exist, run `Probe.exe scan ...` first.

## 4. Scan Workflow

Run scan only when the workspace is missing or obviously stale.

```powershell
.\probe\Probe.exe scan <TARGET_SLN_OR_CSPROJ> --output .\analysis_workspace\<workspace_name>
```

Notes:

- `Probe.exe` still depends on Roslyn / MSBuild being able to resolve the target project.
- Real scans may require .NET SDK, Visual Studio Build Tools, or target-specific Developer Packs / targeting packs.
- If scan fails because the target project environment is incomplete, report that clearly instead of improvising a development workflow.

Main outputs:

- `output/repository.db`
- `output/graphs/call_graph.json`
- `output/graphs/inheritance_graph.json`
- `output/graphs/field_access_graph.json`
- `output/graphs/type_dependency_graph.json`

## 5. Navigation Before Grep

Use these commands first:

```powershell
python .\python-rag\main.py files --workspace .\analysis_workspace\<workspace_name> --limit 30
python .\python-rag\main.py symbols --workspace .\analysis_workspace\<workspace_name> "<name or FQN>"
python .\python-rag\main.py show-hotspots --workspace .\analysis_workspace\<workspace_name> --limit 20
python .\python-rag\main.py show-isolation --workspace .\analysis_workspace\<workspace_name> --limit 20
python .\python-rag\main.py show-isolation --workspace .\analysis_workspace\<workspace_name> --compare-ai-soft-edges
python .\python-rag\main.py graph-meta --workspace .\analysis_workspace\<workspace_name>
python .\python-rag\main.py xaml-candidates --workspace .\analysis_workspace\<workspace_name> --limit 20
python .\python-rag\main.py ai-candidates --workspace .\analysis_workspace\<workspace_name> --kind all --limit 20
python .\python-rag\main.py show-ai-edges --workspace .\analysis_workspace\<workspace_name> --limit 20 --json
```

Use them like this:

- `files`: locate analyzed files and project boundaries
- `symbols`: locate likely entry symbols before opening files
- `show-hotspots`: identify structural centers first
- `show-isolation`: inspect structural outliers as investigation aids
- `graph-meta`: confirm which scan produced the workspace
- `xaml-candidates`: find XAML / code-behind areas worth AI assistance
- `ai-candidates`: find `xaml / reflection / di` areas worth AI assistance
- `show-ai-edges --json`: inspect imported AI soft edge quality

## 6. AI Soft Edge Workflow

When hard graph recovery is weak around XAML, reflection, DI, callbacks, or framework-owned dispatch:

1. Use `xaml-candidates` or `ai-candidates`.
2. Export a bundle with `--bundle-path`.
3. Let another AI read the difficult files and produce `ai_soft_edges.json`.
4. Import the file.
5. Check `show-ai-edges --json`.
6. Opt in to use soft edges for `isolation` or `related` only when needed.

Commands:

```powershell
python .\python-rag\main.py ai-candidates --workspace .\analysis_workspace\<workspace_name> --kind reflection --bundle-path .\candidate_bundle.json
python .\python-rag\main.py import-ai-edges .\ai_soft_edges.json --workspace .\analysis_workspace\<workspace_name>
python .\python-rag\main.py show-ai-edges --workspace .\analysis_workspace\<workspace_name> --json
python .\python-rag\main.py isolation --workspace .\analysis_workspace\<workspace_name> --with-ai-soft-edges
python .\python-rag\main.py related "<known FQN or symbol>" --workspace .\analysis_workspace\<workspace_name> --with-ai-soft-edges
```

Rules:

- `ai_soft_edges.json` is soft evidence, not hard graph.
- Do not promote AI soft edges into the hard graph by assumption.
- Use them to guide reading and reduce blind spots.

## 7. Interpretation

Prioritize these questions:

- Which files or symbols are central?
- Which areas have heavy fan-in or state sharing?
- Which framework-driven areas are likely under-modeled?
- Which difficult files deserve manual or AI-assisted reading?

De-prioritize these mistakes:

- treating `isolation` as dead-code proof
- trusting one heuristic report in isolation
- skipping RepoGraph and jumping straight to file-by-file grep

## 8. When To Escalate

Report clearly when:

- `probe/` distribution looks incomplete
- target project resolution fails because SDK / Build Tools / targeting packs are missing
- workspace is stale and a full rescan is needed
- hard graph coverage is clearly insufficient and `ai-candidates` should be used
