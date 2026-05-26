# RepoGraph: Development Instructions for AI Agent

## 1. Scope

This file is for AI agents that modify RepoGraph itself.

Use this file only when you are:

- editing C# or Python code inside RepoGraph
- changing extraction logic
- updating CLI behavior
- adding tests
- preparing a release

For normal repository analysis with RepoGraph, use `AI_INSTRUCTIONS.md` instead.

## 2. Development Principles

- RepoGraph is a map, not a verdict engine.
- Prefer deterministic hard graph extraction for structural facts.
- Use AI soft edges as a separate overlay, not as silent hard promotion.
- Avoid adding narrow project-specific heuristics unless the rule is likely to generalize.
- Prefer candidate surfacing over forced over-modeling when the domain is highly dynamic.

## 3. Local Development Layout

In this repository, source layout is:

```text
RepoGraph/
  roslyn-cli/
    Probe/
  python-rag/
  rules/
    dsl/
  plan/
  README.md
  AI_INSTRUCTIONS.md
  AI_INSTRUCTIONS_dev.md
```

Key areas:

- `roslyn-cli/Probe`: C# extractor
- `python-rag`: Python analysis and CLI
- `rules/dsl/*.json`: production DSL candidate rules
- `plan/`: working notes and handoff files, not for commit

Corresponding distribution layout:

- development source path: `roslyn-cli/Probe`
- normal distribution path: `probe/`

Do not write normal-use documentation as if end users must know the `roslyn-cli/Probe` source path.

## 4. Build And Run

Use source-based commands only in development mode.

Build:

```powershell
& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" build C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj
```

Run from source:

```powershell
$env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"
& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" run --project C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj -- scan <TARGET_SLN_OR_CSPROJ> --output C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

Python examples:

```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py hotspots --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py show-ai-edges --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --json
```

## 5. Testing

Run focused tests for the area you changed.

Common Python test commands:

```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe -m unittest test_main test_soft_edges
C:\tmp\RepoGraph\.venv\Scripts\python.exe -m unittest test_main test_isolation test_hotspots test_soft_edges test_retrieval test_summarize
```

Run them from:

```text
C:\tmp\RepoGraph\python-rag
```

C# verification:

```powershell
& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" build C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj
```

Test principle:

- Do not add tests that only lock in incidental output.
- Prefer tests that prove metadata is preserved, CLI contracts are stable, and behavior matches intended boundaries.

## 6. Editing Guidance

- Keep `plan/` out of commits.
- Do not commit temporary files from `analysis_workspace/`.
- Keep `rules/dsl/*.json` tracked.
- When changing heuristic logic, ask whether the logic belongs in:
  - structural hard extraction
  - DSL candidate rules (if expressible by the current DSL)
  - AI soft edge overlay (if unsupported package-specific behavior)

## 7. Release Guidance

Typical release preparation includes:

1. update version strings
2. update `README.md` and AI instruction files
3. run relevant tests
4. inspect `git diff --stat`
5. inspect `git status`
6. exclude `plan/` and temporary analysis outputs
7. commit with a release message

## 8. Current Direction

Current architectural direction:

- hard graph stays deterministic
- difficult framework-owned or dynamic edges should often become candidates, not forced hard edges
- C# core should not carry package/framework heuristic catalogs
- external package/framework heuristics are represented as DSL candidates only when expressible by the current DSL
- unsupported package-specific behavior is left to AI/post-processing rather than preserved in core
- RepoGraph should help AI ask better questions, not pretend to fully decide usage or dead code

## 9. Recent Refactoring

- **SymbolExtractor Refactor**: The SymbolExtractor class has been successfully refactored from a single God Class into focused analysis components (DirectCallExtractor, ReflectionDispatchExtractor, TypeDependencyExtractor, etc.). SymbolExtractor now focuses primarily on traversal orchestration. Do not add unrelated new heuristics directly back into SymbolExtractor. Create or update specialized extractors.
