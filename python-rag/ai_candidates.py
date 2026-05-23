import os
from datetime import datetime, timezone

from models import Document, Project, Symbol


AI_CANDIDATE_MARKERS = {
    "reflection": (
        "Activator.CreateInstance",
        ".GetTypes(",
        ".GetExportedTypes(",
        ".GetConstructor(",
        ".Invoke(",
        ".IsAssignableFrom(",
        "Assembly.Load(",
        "Assembly.LoadFrom(",
    ),
    "di": (
        "GetRequiredService<",
        "GetService<",
        ".Resolve<",
        "AddSingleton",
        "AddScoped",
        "AddTransient",
        "RegisterType<",
        "RegisterAssemblyModules",
        ".As<",
        "IServiceCollection",
        "ContainerBuilder",
    ),
}

AI_CANDIDATE_EDGE_HINTS = {
    "xaml": ["ai_xaml_candidate_event", "ai_xaml_candidate_command_binding"],
    "reflection": ["ai_reflection_candidate_dispatch"],
    "di": ["ai_di_candidate_dispatch", "ai_factory_candidate_dispatch"],
}


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def looks_like_ui_callback_fqn(fqn: str) -> bool:
    callback_markers = (
        "RoutedEventArgs",
        "DependencyPropertyChangedEventArgs",
        "ExecutedRoutedEventArgs",
        "CanExecuteRoutedEventArgs",
        "MouseEventArgs",
        "MouseButtonEventArgs",
        "KeyEventArgs",
        "TextChangedEventArgs",
        "SelectionChangedEventArgs",
        "NotifyCollectionChangedEventArgs",
        "DragEventArgs",
        "EventArgs",
    )
    return any(marker in fqn for marker in callback_markers)


def read_text_file(path: str) -> str:
    try:
        with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
            return f.read()
    except OSError:
        return ""


def count_outbound_edge_types(graph, node_ids: list[str], edge_types: set[str]) -> int:
    count = 0
    for node_id in node_ids:
        if node_id not in graph:
            continue
        for _, _, data in graph.out_edges(node_id, data=True):
            if data.get("type") in edge_types:
                count += 1
    return count


def unique_paths(values: list[str | None]) -> list[str]:
    seen: set[str] = set()
    ordered: list[str] = []
    for value in values:
        if not value or value in seen:
            continue
        seen.add(value)
        ordered.append(value)
    return ordered


def select_ai_candidate_symbols(symbols: list[Symbol], kind: str) -> list[Symbol]:
    type_hints = {
        "reflection": ("loader", "factory", "provider", "module", "registry", "resolver", "builder", "activator"),
        "di": ("startup", "module", "registr", "service", "provider", "container", "factory", "builder"),
    }
    method_hints = {
        "reflection": ("load", "create", "build", "resolve", "discover", "get", "activate"),
        "di": ("configure", "register", "resolve", "create", "build", "get"),
    }

    def score(symbol: Symbol) -> tuple[int, int, int, str]:
        type_name = (symbol.containing_type or "").split(".")[-1].lower()
        name = (symbol.name or "").lower()
        score_value = 0
        if symbol.kind == "constructor":
            score_value += 2
        if symbol.fan_in in (None, 0):
            score_value += 4
        elif symbol.fan_in == 1:
            score_value += 2
        if any(token in type_name for token in type_hints[kind]):
            score_value += 3
        if any(name.startswith(prefix) for prefix in method_hints[kind]):
            score_value += 2
        return (-score_value, -(symbol.loc or 0), symbol.fan_in or 0, symbol.fqn)

    filtered = [symbol for symbol in symbols if symbol.kind in {"method", "constructor"}]
    filtered.sort(key=score)
    return filtered[:5]


def build_xaml_candidates(session, graph_loader, limit: int, include_context: bool) -> list[dict]:
    rows = (
        session.query(Symbol, Document, Project)
        .outerjoin(Document, Symbol.document_id == Document.id)
        .join(Project, Symbol.project_id == Project.id)
        .filter(Symbol.kind == "xaml")
        .all()
    )
    payload = []

    for xaml_symbol, document, project_row in rows:
        xaml_fqn = xaml_symbol.fqn
        outbound_calls = list(graph_loader.call_graph.out_edges(xaml_fqn, data=True)) if xaml_fqn in graph_loader.call_graph else []
        outbound_types = list(graph_loader.type_dependency_graph.out_edges(xaml_fqn, data=True)) if xaml_fqn in graph_loader.type_dependency_graph else []

        event_edges = [edge for edge in outbound_calls if edge[2].get("type") in {"xaml_event", "xaml_action_binding"}]
        command_edges = [edge for edge in outbound_types if edge[2].get("type") in {"xaml_command_binding", "xaml_navigation"}]

        containing_type = xaml_symbol.containing_type or ""
        related_methods = (
            session.query(Symbol, Document)
            .outerjoin(Document, Symbol.document_id == Document.id)
            .filter(Symbol.kind == "method", Symbol.containing_type == containing_type)
            .all()
        )

        weak_handlers = []
        for method_symbol, method_document in related_methods:
            if method_symbol.fan_in and method_symbol.fan_in > 0:
                continue
            if not looks_like_ui_callback_fqn(method_symbol.fqn):
                continue
            weak_handlers.append({
                "fqn": method_symbol.fqn,
                "file_path": method_document.file_path if method_document else None,
                "loc": method_symbol.loc,
            })

        reasons = []
        score = 0
        if len(event_edges) == 0:
            reasons.append("xaml has no recovered event/action edges")
            score += 3
        if len(command_edges) == 0:
            reasons.append("xaml has no recovered command/navigation edges")
            score += 2
        if weak_handlers:
            reasons.append(f"{len(weak_handlers)} likely UI callback(s) still have fan_in=0")
            score += min(len(weak_handlers), 5)
        file_path = document.file_path if document else ""
        if "\\Shared\\" in file_path or file_path.endswith(".xaml"):
            reasons.append("shared or resource-linked XAML can hide ownership boundaries")
            score += 1

        if score == 0:
            continue

        item = {
            "candidate_kind": "xaml",
            "xaml_fqn": xaml_fqn,
            "project": project_row.name,
            "file_path": file_path,
            "containing_type": containing_type,
            "event_edges": len(event_edges),
            "command_edges": len(command_edges),
            "weak_handlers": weak_handlers[:10],
            "reasons": reasons,
            "score": score,
        }
        if include_context:
            item["context_files"] = unique_paths([file_path] + [handler["file_path"] for handler in weak_handlers[:10]])
            item["suggested_soft_edge_types"] = AI_CANDIDATE_EDGE_HINTS["xaml"]
            item["context_symbols"] = [xaml_fqn] + [handler["fqn"] for handler in weak_handlers[:5]]
        payload.append(item)

    payload.sort(key=lambda item: (-item["score"], item["xaml_fqn"]))
    return payload[:limit]


def build_non_xaml_ai_candidates(session, graph_loader, kind: str, limit: int) -> list[dict]:
    edge_types = {
        "reflection": {"reflection_constructor_dispatch"},
        "di": {"service_provider_dispatch", "autofac_resolve_dispatch", "autofac_module_load"},
    }
    payload = []

    rows = session.query(Document, Project).join(Project, Document.project_id == Project.id).all()
    for document, project_row in rows:
        if getattr(project_row, "is_test_project", 0) == 1:
            continue

        file_path = document.file_path or ""
        if not file_path.lower().endswith(".cs"):
            continue

        text = read_text_file(file_path)
        if not text:
            continue

        pattern_hits = [marker for marker in AI_CANDIDATE_MARKERS[kind] if marker in text]
        if not pattern_hits:
            continue

        symbols = session.query(Symbol).filter(Symbol.document_id == document.id).all()
        focal_symbols = select_ai_candidate_symbols(symbols, kind)
        if not focal_symbols:
            continue

        focal_fqns = [symbol.fqn for symbol in focal_symbols]
        hard_edge_count = count_outbound_edge_types(graph_loader.call_graph, focal_fqns, edge_types[kind])
        isolated_count = sum(1 for symbol in focal_symbols if symbol.fan_in in (None, 0))

        reasons = [f"file contains {len(pattern_hits)} {kind} marker(s)"]
        score = len(pattern_hits)
        if hard_edge_count == 0:
            reasons.append(f"no recovered hard {kind} dispatch edges from focal symbols")
            score += 3
        elif hard_edge_count <= 2:
            reasons.append(f"only {hard_edge_count} recovered hard {kind} dispatch edge(s)")
            score += 1

        if isolated_count:
            reasons.append(f"{isolated_count} focal symbol(s) still have fan_in=0")
            score += min(isolated_count, 3)

        payload.append({
            "candidate_kind": kind,
            "project": project_row.name,
            "file_path": file_path,
            "context_files": [file_path],
            "pattern_hits": pattern_hits[:8],
            "hard_edge_count": hard_edge_count,
            "reasons": reasons,
            "score": score,
            "suggested_soft_edge_types": AI_CANDIDATE_EDGE_HINTS[kind],
            "focal_symbols": [
                {
                    "fqn": symbol.fqn,
                    "kind": symbol.kind,
                    "fan_in": symbol.fan_in,
                    "loc": symbol.loc,
                }
                for symbol in focal_symbols
            ],
            "context_symbols": [symbol.fqn for symbol in focal_symbols[:5]],
        })

    payload.sort(key=lambda item: (-item["score"], item["file_path"]))
    return payload[:limit]


def build_ai_candidates(session, graph_loader, kind: str, limit: int) -> list[dict]:
    payload = []
    if kind in {"all", "xaml"}:
        payload.extend(build_xaml_candidates(session, graph_loader, limit if kind == "xaml" else max(limit, 50), include_context=True))
    if kind in {"all", "reflection"}:
        payload.extend(build_non_xaml_ai_candidates(session, graph_loader, "reflection", limit if kind == "reflection" else max(limit, 50)))
    if kind in {"all", "di"}:
        payload.extend(build_non_xaml_ai_candidates(session, graph_loader, "di", limit if kind == "di" else max(limit, 50)))

    payload.sort(key=lambda item: (-item["score"], item.get("candidate_kind", ""), item.get("file_path", "")))
    return payload[:limit]


def build_ai_candidate_bundle(workspace: str | None, kind: str, payload: list[dict]) -> dict:
    return {
        "bundle_schema_version": 1.1,
        "generated_at": utc_timestamp(),
        "workspace": workspace,
        "kind": kind,
        "usage_notes": (
            "These are AI-escalation candidates. Treat them as soft review targets, not as confirmed graph edges. "
            "They indicate areas where the hard graph may be incomplete. "
            "Prefer returning ai_soft_edges.json entries only when you can explain the evidence."
        ),
        "rule_mode_legend": {
            "HardEdge": "Confirmed by strict C# parsing rules.",
            "Candidate": "Heuristically flagged as suspicious but unconfirmed. Requires AI or manual review."
        },
        "rule_family_summary": "Candidates typically involve families like 'xaml', 'reflection', or 'di'. Use the 'rules' CLI to view the catalog.",
        "recommended_commands": [
            "python-rag/main.py rules --json",
            f"python-rag/main.py symbols --workspace {workspace or '<workspace>'} \"<fqn>\"",
            f"python-rag/main.py import-ai-edges <soft_edges.json> --workspace {workspace or '<workspace>'}"
        ],
        "candidates": payload,
    }
