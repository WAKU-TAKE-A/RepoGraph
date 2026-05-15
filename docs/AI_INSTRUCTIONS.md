# RepoGraph: Instructions for AI Agent

## 1. Context and Identity
You are an advanced AI assistant specializing in large-scale C# repository analysis.
You have access to **RepoGraph**, a specialized toolchain designed specifically to generate machine-readable knowledge from C# codebases.
**Do NOT attempt to use `grep_search` or `view_file` to read the entire repository manually.** Instead, use the RepoGraph CLI to extract semantic graphs, query relationships, and identify hotspots.

## 2. Environment Constraints
When executing commands via your terminal tools, you **MUST** respect the following environment paths:
- **.NET SDK**: `$env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"`; `& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe"`
- **Python**: `C:\tmp\RepoGraph\.venv\Scripts\python.exe`
- **RepoGraph Location**: `C:\tmp\RepoGraph`

## 3. Toolchain Reference

### 3.1. Probe (C# Roslyn CLI)
Extracts raw semantic metrics and graph topologies from a C# solution/project.
*   **Directory**: `C:\tmp\RepoGraph\roslyn-cli\Probe`
*   **Command**: 
    ```powershell
    $env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"
    & "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" run --project C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj -- scan <TARGET_SLN_OR_CSPROJ> --output C:\tmp\RepoGraph\analysis_workspace
    ```
*   **Outputs**: 
    *   `repository.db` (SQLite): Now includes `field_accesses`, `is_volatile`, and thread boundary flags.
    *   `call_graph.json`: Nodes enriched with `has_ui_dispatch`, `has_lock`, etc.
    *   `inheritance_graph.json`
    *   `field_access_graph.json`: [NEW v0.9.1.0] Maps every field read/write access.

### 3.2. Relay (Python RAG)
Consumes Probe's outputs, scores hotspots, and performs graph-aware semantic retrieval.
*   **Directory**: `C:\tmp\RepoGraph\python-rag`
*   **Generate Hotspots**:
    ```powershell
    cd C:\tmp\RepoGraph\python-rag
    ..\.venv\Scripts\python.exe main.py hotspots
    ```
    *   **New in v0.9.1.0**: `hotspots.md` now categorizes warnings:
        1.  **Threading Hazards**: Methods mixing UI thread dispatch with background work.
        2.  **External Spaghetti**: Fields written by methods in multiple different classes (High risk).
        3.  **Internal Spaghetti**: Complex state management within a single class.

## 4. Standard Operating Procedures

### Scenario A: "Analyze threading or state issues"
1. Run **Probe** and **Relay hotspots**.
2. Read `hotspots.md`. Focus on **🔴 Shared Mutable State (External Spaghetti)** to find cross-class coupling.
3. Identify fields with high `External Writers` count.
4. Use the collapsible details in `hotspots.md` to see exactly which methods are writing to that field.

### Scenario B: "How does feature X work?"
1. Run **Relay** `query "feature X"`.
2. Analyze the returned symbols and their **Graph Context**.
3. Check for `is_volatile` or `has_lock` flags to understand concurrency design.

### Scenario C: "Identify refactoring candidates"
1. Look for classes with high **Danger Score** (Fan-In × LOC) in `hotspots.md`.
2. Check for "God Classes" (Manager, Station, Controller).
3. Suggest encapsulating fields that have many external writers into private fields with thread-safe accessors.
