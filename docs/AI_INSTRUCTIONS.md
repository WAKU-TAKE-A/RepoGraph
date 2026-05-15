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
    *   `field_access_graph.json`: Maps every field read/write access.
    *   **Hotspots Logic (v0.9.2.0)**: Shared state writers are now categorized into **Init**, **Event**, and **Runtime** contexts.

### 3.2. Relay (Python RAG)
Consumes Probe's outputs, scores hotspots, and performs graph-aware semantic retrieval.
*   **Directory**: `C:\tmp\RepoGraph\python-rag`
*   **Generate Hotspots**:
    ```powershell
    cd C:\tmp\RepoGraph\python-rag
    ..\.venv\Scripts\python.exe main.py hotspots
    ```
    *   **New in v0.9.2.0**: `hotspots.md` segregates Shared Mutable State by risk:
        1.  **🔴 High Risk (Runtime/Event)**: Fields mutated during business logic (`MyRun`, `Execute`) or UI events (`_Click`). Priority 1.
        2.  **ℹ️ Low Risk (Init-only)**: Fields mutated only during initialization (`_Load`, `.ctor`). Likely wiring.
        3.  **Risk Score**: Weighted scoring system: `Runtime (10x)`, `Event (3x)`, `Init (0.1x)`.
        4.  **Threading Hazards**: Methods mixing UI thread dispatch with background work.

## 4. Standard Operating Procedures

### Scenario A: "Analyze threading or state issues"
1. Run **Probe** and **Relay hotspots**.
2. Read `hotspots.md`. Focus on **🔴 Shared Mutable State (Runtime/Event — High Risk)** to find cross-class coupling.
3. Identify fields with high **Risk Score** (weighted towards runtime mutations).
4. Use the collapsible details in `hotspots.md` to see exactly which **Runtime writers** are mutating that field.

### Scenario B: "How does feature X work?"
1. Run **Relay** `query "feature X"`.
2. Analyze the returned symbols and their **Graph Context**.
3. Check for `is_volatile` or `has_lock` flags to understand concurrency design.

### Scenario C: "Identify refactoring candidates"
1. Look for classes with high **Danger Score** (Fan-In × LOC) in `hotspots.md`.
2. Check for "God Classes" (Manager, Station, Controller).
3. Suggest encapsulating fields that have many external writers into private fields with thread-safe accessors.
