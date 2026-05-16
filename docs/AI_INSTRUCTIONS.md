# RepoGraph: Instructions for AI Agent

## 1. Role
You are analyzing a C# / .NET repository with RepoGraph.
RepoGraph is useful for structural evidence gathering, but it is not an oracle.
Treat `hotspots`, graphs, and semantic search as primary evidence.
Treat `deadcode` as a secondary, heuristic aid that always requires follow-up verification.

## 2. Environment
Use these exact paths when running commands:

- `.NET SDK`
  `$env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"`
  `& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe"`
- `Python`
  `C:\tmp\RepoGraph\.venv\Scripts\python.exe`
- `RepoGraph root`
  `C:\tmp\RepoGraph`

## 3. Primary Workflow

### 3.1 Scan with Probe
```powershell
$env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"
& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" run --project C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj -- scan <TARGET_SLN_OR_CSPROJ> --output C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

Main outputs:
- `output/repository.db`
- `output/graphs/call_graph.json`
- `output/graphs/inheritance_graph.json`
- `output/graphs/field_access_graph.json`
- `output/graphs/type_dependency_graph.json`

### 3.2 Generate Hotspots
```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py hotspots --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

Read `hotspots.md` in this order:
1. `Anti-Pattern Warnings`
2. `Threading Hazard Warnings`
3. `Shared Mutable State`
4. `Top 50 Hotspots`

### 3.3 Optional: Query
```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py query "<concept>" --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

### 3.4 Optional: Dead Code Candidates
```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py deadcode --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

Important:
- `deadcode` is not a final decision tool.
- Use it to narrow search space, not to justify deletion by itself.
- Always confirm with graph evidence, text search, and surrounding code.

## 4. Recommended Interpretation

### When investigating architecture or risk
Prioritize:
- high fan-in classes and methods
- shared mutable state with many runtime writers
- UI dispatch mixed with background execution
- type and field access concentration

### When investigating unused code
Use this sequence:
1. check `dead_code_candidates.md`
2. inspect `call_graph.json`, `type_dependency_graph.json`, and `field_access_graph.json`
3. search for framework-driven references, reflection, DI registration, events, lambdas, or external entrypoints
4. only then judge whether the candidate is likely unused

## 5. Current Strengths and Limits

### Stronger areas
- hotspot ranking
- threading hazard detection
- shared mutable state detection
- lambda / event / type dependency recovery

### Weaker areas
- reflection-heavy dispatch
- framework conventions not yet modeled
- some command / host / callback-driven execution paths
- dead code precision in highly dynamic systems

## 6. Behavioral Guidance
- Do not manually read the whole repository first.
- Use RepoGraph outputs to choose where to read.
- Prefer evidence from multiple signals over a single report.
- If `deadcode` conflicts with `hotspots` or obvious framework structure, trust `deadcode` less.
