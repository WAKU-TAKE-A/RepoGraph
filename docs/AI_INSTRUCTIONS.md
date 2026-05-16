# RepoGraph: Instructions for AI Agent

## 1. Context and Identity
You are an advanced AI assistant specializing in large-scale C# repository analysis.
You have access to **RepoGraph**, a specialized toolchain designed specifically to generate machine-readable knowledge from C# codebases.
**Do NOT attempt to use `grep_search` or `view_file` to read the entire repository manually.** Instead, use the RepoGraph CLI to extract semantic graphs, query relationships, identify hotspots, and perform scoped semantic searches.

## 2. Environment Constraints
When executing commands via your terminal tools, you **MUST** respect the following environment paths:
- **.NET SDK**: `$env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"`; `& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe"`
- **Python**: `C:\tmp\RepoGraph\.venv\Scripts\python.exe`
- **RepoGraph Location**: `C:\tmp\RepoGraph`

## 3. Toolchain Reference

### 3.1. Probe (C# Roslyn CLI)
Extracts raw semantic metrics, runtime thread contexts, and graph topologies from a C# solution/project.
* **Directory**: `C:\tmp\RepoGraph\roslyn-cli\Probe`
* **Command**: 
    ```powershell
    $env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"
    & "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" run --project C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj -- scan <TARGET_SLN_OR_CSPROJ> --output C:\tmp\RepoGraph\analysis_workspace
    ```
* **Outputs**: All artifacts are generated under `<output>/output/` (e.g., `analysis_workspace/output/`):
    * `repository.db` (SQLite): Stores structured metadata including `field_accesses`, `is_volatile`, and thread boundary flags.
    * `graphs/dependency_graph.json`: Project-to-project references.
    * `graphs/call_graph.json`: Enriched with execution topology (`has_ui_dispatch`, `has_lock`, `has_blocking_wait`, etc.).
    * `graphs/inheritance_graph.json`: Class and interface hierarchies.
    * `graphs/field_access_graph.json`: Comprehensive map of field read/write interactions across class boundaries.

### 3.2. Relay (Python RAG)
Consumes Probe's outputs to score structural risks and perform graph-aware, metadata-filtered semantic retrieval.
* **Directory**: `C:\tmp\RepoGraph\python-rag`
* **Generate Hotspots**:
    ```powershell
    cd C:\tmp\RepoGraph\python-rag
    ..\.venv\Scripts\python.exe main.py hotspots
    ```
    * `hotspots.md` splits metrics into highly actionable risk categories:
        1. **⚠️ Anti-Pattern Warnings (God Class)**: High `Danger Score` (Fan-In × LOC) anomalies.
        2. **🔴 Threading Hazard Warnings**: Risks involving UI dispatch mixed with blocking waits or `DoEvents()`.
        3. **🔴 Shared Mutable State (High Risk)**: Cross-class mutations during business logic (`Runtime` / `Event` contexts).
        4. **ℹ️ Shared Mutable State (Low Risk)**: UI bindings and static initialization wiring (`Init` contexts).
* **Query Codebase (v0.9.4.0 Hybrid Search)**:
    ```powershell
    cd C:\tmp\RepoGraph\python-rag
    ..\.venv\Scripts\python.exe main.py query "camera connection initialization" --kind method --project 148bc5a7-52eb-474c-a764-3212807ef32b
    ```
    * Performance is backed by a scalable **FAISS HNSW (IndexHNSWFlat)** approximate nearest neighbor graph.
    * Use optional flags `--kind` (`method`, `class`, `property`, etc.) and `--project` (`[PROJECT_GUID]`) to trigger mathematically strict compound pre-filtering via `IDSelectorArray`.

## 4. Standard Operating Procedures

### Scenario A: "Analyze threading, re-entrancy, or state synchronization bugs"
1. Run **Probe** to capture full runtime context, then generate **Relay hotspots**.
2. Open `hotspots.md` and immediately jump to **🔴 Shared Mutable State (Cross-Class — External Spaghetti)**.
3. Target fields with high `External Writers` counts and high contextual weights.
4. Expand the collapsible markdown details (`<details><summary>`) to inspect the specific setter methods contributing to the coupling.
5. Cross-reference with the **🔴 Threading Hazard Warnings** table to check if those writers bypass UI thread dispatch boundaries or utilize unsafe blocking waits.

### Scenario B: "Trace how a feature or concept is implemented"
1. Execute **Relay** `query "<concept_keywords>"`.
2. To narrow your semantic space and avoid clutter, combine attributes: e.g., use `--kind method` to find core behavioral routines, or add `--project <id>` to lock the search to a specific module.
3. Review the matching symbols alongside their **Graph Context** block (automatically populated with 2-hop caller/callee and inheritance hierarchies via NetworkX).
4. Identify entry-point classes and navigate the topology using the structural edges provided.

### Scenario C: "Identify and execute refactoring strategies"
1. Locate architectural anchors using the **God Class** ranking (highest Danger Score).
2. Isolate behavioral logic from state management: identify encapsulated fields subjected to external writes, and design thread-safe wrappers or unified service boundaries.
3. Avoid raw file text modification without checking downstream impacts; consult the structural graphs to find all external subscribers before altering signatures.