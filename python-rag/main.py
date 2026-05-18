import typer
import yaml
import os
import json
from datetime import datetime, timezone
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from loguru import logger
from models import Base, Document, Project, Symbol
from graph import GraphLoader
from hotspots import HotspotScorer
from deadcode import DeadCodeDetector
from related import RelatedFinder
import networkx as nx

app = typer.Typer()


def resolve_workspace_paths(workspace: str | None) -> dict[str, str]:
    config = load_config()
    output_dir = os.path.join(workspace, "output") if workspace else config.get("output", {}).get("directory", "")
    return {
        "db_path": os.path.join(workspace, "output", "repository.db") if workspace else config.get("database", {}).get("path", ""),
        "graphs_dir": os.path.join(workspace, "output", "graphs") if workspace else config.get("graphs", {}).get("directory", ""),
        "reports_dir": os.path.join(workspace, "output", "reports") if workspace else os.path.join(output_dir, "reports"),
    }


def open_session(db_path: str):
    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    return engine, Session()


def load_json_file(path: str) -> dict:
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def require_file(path: str, label: str) -> bool:
    if os.path.exists(path):
        return True
    logger.error(f"{label} not found at {path}")
    return False


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def normalize_ai_soft_edge(edge: dict) -> dict:
    source = str(edge.get("source", "")).strip()
    target = str(edge.get("target", "")).strip()
    edge_type = str(edge.get("type", "ai_soft_edge")).strip() or "ai_soft_edge"
    if not source or not target:
        raise ValueError("Each AI soft edge needs non-empty source and target.")

    confidence = edge.get("confidence")
    if confidence is not None:
        confidence = float(confidence)
        if confidence < 0.0 or confidence > 1.0:
            raise ValueError("confidence must be between 0.0 and 1.0.")

    evidence = edge.get("evidence", [])
    if evidence is None:
        evidence = []
    if not isinstance(evidence, list):
        raise ValueError("evidence must be a list.")

    notes = edge.get("notes", "")
    return {
        "source": source,
        "target": target,
        "type": edge_type,
        "confidence": confidence,
        "evidence": [str(item) for item in evidence],
        "notes": str(notes) if notes is not None else "",
    }


def load_ai_soft_payload(import_path: str) -> dict:
    payload = load_json_file(import_path)
    raw_edges = payload.get("edges", payload if isinstance(payload, list) else [])
    if not isinstance(raw_edges, list):
        raise ValueError("AI soft edge payload must be a list or an object with an 'edges' list.")

    normalized_edges = [normalize_ai_soft_edge(edge) for edge in raw_edges]
    return {
        "schema_version": payload.get("schema_version", 1) if isinstance(payload, dict) else 1,
        "generated_by": payload.get("generated_by", "external_ai") if isinstance(payload, dict) else "external_ai",
        "notes": payload.get("notes", "") if isinstance(payload, dict) else "",
        "edges": normalized_edges,
    }


def merge_ai_soft_payload(existing_payload: dict, new_payload: dict) -> dict:
    merged: dict[tuple[str, str, str], dict] = {}
    for payload in (existing_payload, new_payload):
        for edge in payload.get("edges", []):
            normalized = normalize_ai_soft_edge(edge)
            merged[(normalized["source"], normalized["target"], normalized["type"])] = normalized

    return {
        "schema_version": max(existing_payload.get("schema_version", 1), new_payload.get("schema_version", 1)),
        "generated_by": "repograph.import_ai_edges",
        "notes": new_payload.get("notes") or existing_payload.get("notes", ""),
        "edges": sorted(
            merged.values(),
            key=lambda item: (item["source"], item["target"], item["type"]),
        ),
    }


def compute_deadcode_snapshot(session, graph_loader: GraphLoader, reports_dir: str, include_ai_soft_edges: bool) -> dict:
    detector = DeadCodeDetector(session, graph_loader, reports_dir, include_ai_soft_edges=include_ai_soft_edges)
    candidates = detector.detect_dead_code_candidates()
    return {
        "count": len(candidates),
        "candidate_fqns": {candidate["fqn"] for candidate in candidates},
        "suppressed_by_ai_soft_edges": getattr(detector, "_suppressed_by_ai_soft_edges", []),
    }

def load_config():
    config_path = "rag_config.yml"
    if not os.path.exists(config_path):
        return {}
    with open(config_path, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)

@app.command()
def doctor(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Verify environment and inputs."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    
    if not os.path.exists(db_path):
        logger.error(f"Database not found at {db_path}")
        return
    
    try:
        engine = create_engine(f"sqlite:///{db_path}")
        with engine.connect() as conn:
            logger.info("Database connection successful.")
    except Exception as e:
        logger.error(f"Database connection failed: {e}")
        return

    logger.info("Environment check completed successfully.")

@app.command()
def build_index(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Build embedding + FAISS index."""
    from summarize import Summarizer
    from index import SentenceTransformerProvider, FaissIndexer

    config = load_config()
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]
    out_dir = os.path.join(workspace, "output", "embeddings") if workspace else config.get("output", {}).get("embeddings_dir", "")

    model_name = config.get("embedding", {}).get("model", "sentence-transformers/all-MiniLM-L6-v2")
    batch_size = config.get("embedding", {}).get("batch_size", 64)
    device = config.get("embedding", {}).get("device", "cpu")
    
    faiss_config = config.get("faiss", {})
    index_type = faiss_config.get("index_type", "HNSW")
    hnsw_m = faiss_config.get("hnsw_m", 32)
    
    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()
    
    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    session = Session()
    
    summarizer = Summarizer(graph_loader)
    
    provider = SentenceTransformerProvider(model_name, device=device)
    indexer = FaissIndexer(provider, out_dir, index_type=index_type, hnsw_m=hnsw_m)
    
    indexer.build_index(session, summarizer, batch_size=batch_size)
    
    logger.info("Index build completed successfully.")

@app.command()
def query(
    text: str, 
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    filter_kind: str = typer.Option(None, "--kind", "-k", help="Filter by symbol kind (e.g., method, class)"),
    filter_project: str = typer.Option(None, "--project", "-p", help="Filter by project ID")
):
    """Semantic + graph-aware query."""
    from index import SentenceTransformerProvider
    from retrieval import Retriever

    config = load_config()
    paths = resolve_workspace_paths(workspace)
    graphs_dir = paths["graphs_dir"]
    out_dir = os.path.join(workspace, "output", "embeddings") if workspace else config.get("output", {}).get("embeddings_dir", "")
    
    model_name = config.get("embedding", {}).get("model", "sentence-transformers/all-MiniLM-L6-v2")
    device = config.get("embedding", {}).get("device", "cpu")
    top_k = config.get("retrieval", {}).get("top_k", 20)
    depth = config.get("retrieval", {}).get("graph_expansion_depth", 2)

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()
    
    provider = SentenceTransformerProvider(model_name, device=device)
    retriever = Retriever(provider, out_dir, graph_loader)
    
    results = retriever.search(text, top_k=top_k, expansion_depth=depth, filter_kind=filter_kind, filter_project_id=filter_project)
    
    if not results:
        logger.warning("No results found.")
        return
        
    for i, res in enumerate(results):
        print(f"\n--- Result {i+1} (Score: {res['score']:.4f}) ---")
        print(f"FQN: {res['fqn']} (Kind: {res.get('kind', 'N/A')}, Project: {res.get('project_id', 'N/A')})")
        print(f"Summary: {res['summary']}")
        if res['context']:
            print("Graph Context:")
            for ctx in res['context']:
                print(f"  - {ctx}")

@app.command()
def hotspots(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Compute and display hotspot rankings."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]
    reports_dir = paths["reports_dir"]

    engine, session = open_session(db_path)

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()

    scorer = HotspotScorer(session, graph_loader, reports_dir)
    scorer.generate_reports()
    session.close()
    engine.dispose()

@app.command()
def deadcode(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    with_ai_soft_edges: bool = typer.Option(False, "--with-ai-soft-edges", help="Treat imported AI soft edges as additional inbound evidence"),
):
    """Generate a list of dead code candidates."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]
    reports_dir = paths["reports_dir"]

    engine, session = open_session(db_path)

    # Load graphs (including new type_dependency_graph if available from C# side)
    graph_loader = GraphLoader(graphs_dir)
    try:
        # Graceful load in case C# side isn't updated yet
        graph_loader.load_all() 
        if with_ai_soft_edges:
            graph_loader.load_ai_soft_edges(reports_dir)
        if not hasattr(graph_loader, 'type_dependency_graph'):
            logger.warning("type_dependency_graph not found. Dead code detection will proceed with Call and Inheritance graphs only.")
    except Exception as e:
        logger.error(f"Error loading graphs: {e}")

    detector = DeadCodeDetector(session, graph_loader, reports_dir, include_ai_soft_edges=with_ai_soft_edges)
    detector.generate_report()
    session.close()
    engine.dispose()

@app.command()
def related(
    symbol: str = typer.Argument(..., help="FQN or symbol name to find related code for"),
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    top_k: int = typer.Option(10, "--top-k", "-n", help="Maximum number of related symbols"),
    same_kind_only: bool = typer.Option(True, "--same-kind/--all-kinds", help="Prefer symbols of the same kind"),
    with_ai_soft_edges: bool = typer.Option(False, "--with-ai-soft-edges", help="Include imported AI soft edges in caller/callee similarity"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Find structurally and lexically related symbols for a known method/class."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]

    engine, session = open_session(db_path)

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()
    if with_ai_soft_edges:
        graph_loader.load_ai_soft_edges(paths["reports_dir"])

    finder = RelatedFinder(session, graph_loader, include_ai_soft_edges=with_ai_soft_edges)
    source_symbol = finder.find_symbol(symbol)
    if source_symbol is None:
        logger.warning("Symbol not found: {}", symbol)
        suggestions = finder.suggest_matches(symbol)
        if suggestions:
            print("Closest matches:")
            for suggestion in suggestions:
                print(f"  - {suggestion}")
        return

    print(f"Source: {source_symbol.fqn} ({source_symbol.kind})")
    results = finder.find_related(source_symbol, top_k=top_k, same_kind_only=same_kind_only)
    if not results:
        logger.warning("No related symbols found.")
        return

    if json_output:
        payload = {
            "source": {
                "fqn": source_symbol.fqn,
                "kind": source_symbol.kind,
            },
            "results": [result.to_dict() for result in results],
        }
        print(json.dumps(payload, indent=2, ensure_ascii=False))
        return

    for index, result in enumerate(results, start=1):
        print(f"\n--- Related {index} (score: {result.score:.4f}) ---")
        print(f"FQN: {result.fqn} (Kind: {result.kind})")
        print("Reasons:")
        for reason in result.reasons[:6]:
            print(f"  - {reason}")
    session.close()
    engine.dispose()


@app.command()
def files(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    project: str = typer.Option(None, "--project", "-p", help="Filter by project name or project id"),
    pattern: str = typer.Option(None, "--pattern", help="Filter by substring in file path or file name"),
    limit: int = typer.Option(100, "--limit", "-n", help="Maximum files to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """List analyzed files so AI can start from RepoGraph instead of grep."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    if not require_file(db_path, "Database"):
        return

    engine, session = open_session(db_path)
    query = session.query(Document, Project).join(Project, Document.project_id == Project.id)

    if project:
        project_lower = project.lower()
        query = query.filter((Project.name.ilike(f"%{project_lower}%")) | (Project.id.ilike(f"%{project_lower}%")))

    if pattern:
        query = query.filter((Document.file_path.ilike(f"%{pattern}%")) | (Document.file_name.ilike(f"%{pattern}%")))

    rows = query.order_by(Project.name, Document.file_path).limit(limit).all()
    payload = [
        {
            "project": project_row.name,
            "project_id": project_row.id,
            "file_name": document.file_name,
            "file_path": document.file_path,
        }
        for document, project_row in rows
    ]

    if json_output:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
    else:
        for item in payload:
            print(f"[{item['project']}] {item['file_path']}")

    session.close()
    engine.dispose()


@app.command()
def symbols(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    query_text: str = typer.Argument("", help="Optional substring to match against FQN or symbol name"),
    kind: str = typer.Option(None, "--kind", "-k", help="Filter by symbol kind"),
    project: str = typer.Option(None, "--project", "-p", help="Filter by project name or project id"),
    file_pattern: str = typer.Option(None, "--file", help="Filter by file path substring"),
    limit: int = typer.Option(100, "--limit", "-n", help="Maximum symbols to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """List analyzed symbols so AI can navigate from RepoGraph before raw grep."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    if not require_file(db_path, "Database"):
        return

    engine, session = open_session(db_path)
    symbol_query = session.query(Symbol, Project, Document).join(Project, Symbol.project_id == Project.id).outerjoin(Document, Symbol.document_id == Document.id)

    if query_text:
        symbol_query = symbol_query.filter((Symbol.fqn.ilike(f"%{query_text}%")) | (Symbol.name.ilike(f"%{query_text}%")))
    if kind:
        symbol_query = symbol_query.filter(Symbol.kind == kind)
    if project:
        symbol_query = symbol_query.filter((Project.name.ilike(f"%{project}%")) | (Project.id.ilike(f"%{project}%")))
    if file_pattern:
        symbol_query = symbol_query.filter(Document.file_path.ilike(f"%{file_pattern}%"))

    rows = symbol_query.order_by(Symbol.fan_in.desc().nullslast(), Symbol.loc.desc().nullslast(), Symbol.fqn).limit(limit).all()
    payload = [
        {
            "fqn": symbol.fqn,
            "name": symbol.name,
            "kind": symbol.kind,
            "project": project_row.name,
            "file_path": document.file_path if document else None,
            "line_start": symbol.line_start,
            "loc": symbol.loc,
            "fan_in": symbol.fan_in,
        }
        for symbol, project_row, document in rows
    ]

    if json_output:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
    else:
        for item in payload:
            location = f"{item['file_path']}:{item['line_start']}" if item["file_path"] and item["line_start"] else item["file_path"] or "(no file)"
            print(f"[{item['kind']}] {item['fqn']} | project={item['project']} | fan_in={item['fan_in']} | loc={item['loc']} | {location}")

    session.close()
    engine.dispose()


@app.command("import-ai-edges")
def import_ai_edges(
    import_path: str = typer.Argument(..., help="JSON file containing AI soft edges"),
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    replace: bool = typer.Option(False, "--replace", help="Replace existing ai_soft_edges.json instead of merging"),
):
    """Import AI-derived soft edges into output/reports/ai_soft_edges.json."""
    paths = resolve_workspace_paths(workspace)
    reports_dir = paths["reports_dir"]
    os.makedirs(reports_dir, exist_ok=True)
    output_path = os.path.join(reports_dir, "ai_soft_edges.json")

    if not require_file(import_path, "AI soft edge input"):
        raise typer.Exit(code=1)

    try:
        new_payload = load_ai_soft_payload(import_path)
    except Exception as e:
        logger.error(f"Failed to import AI soft edges: {e}")
        raise typer.Exit(code=1)

    if not replace and os.path.exists(output_path):
        existing_payload = load_json_file(output_path)
        merged_payload = merge_ai_soft_payload(existing_payload, new_payload)
    else:
        merged_payload = new_payload

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(merged_payload, f, indent=2, ensure_ascii=False)

    print(json.dumps({
        "path": output_path,
        "edge_count": len(merged_payload["edges"]),
        "replace": replace,
    }, indent=2, ensure_ascii=False))


@app.command("show-ai-edges")
def show_ai_edges(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum AI soft edges to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Show imported AI soft edges from output/reports/ai_soft_edges.json."""
    paths = resolve_workspace_paths(workspace)
    report_path = os.path.join(paths["reports_dir"], "ai_soft_edges.json")
    if not require_file(report_path, "AI soft edge report"):
        return

    payload = load_json_file(report_path)
    edges = payload.get("edges", [])
    edges = sorted(
        edges,
        key=lambda edge: (-float(edge.get("confidence") or 0.0), edge.get("source", ""), edge.get("target", "")),
    )[:limit]

    if json_output:
        print(json.dumps({"edges": edges}, indent=2, ensure_ascii=False))
        return

    for edge in edges:
        confidence = edge.get("confidence")
        confidence_text = f"{confidence:.2f}" if isinstance(confidence, float) else "n/a"
        print(f"[{edge.get('type', 'ai_soft_edge')}] confidence={confidence_text} {edge['source']} -> {edge['target']}")
        for evidence in edge.get("evidence", [])[:3]:
            print(f"  - {evidence}")


@app.command("show-hotspots")
def show_hotspots(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum hotspots to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Show top hotspot entries from the last generated report."""
    paths = resolve_workspace_paths(workspace)
    report_path = os.path.join(paths["reports_dir"], "hotspots.json")
    if not require_file(report_path, "Hotspot report"):
        return

    payload = load_json_file(report_path)
    hotspots_payload = payload.get("hotspots", [])[:limit]
    if json_output:
        print(json.dumps(hotspots_payload, indent=2, ensure_ascii=False))
        return

    for index, item in enumerate(hotspots_payload, start=1):
        metrics = item.get("metrics", {})
        print(
            f"{index}. [{item.get('kind')}] {item.get('fqn')} | score={item.get('score')} | "
            f"fan_in={metrics.get('fan_in')} | loc={metrics.get('loc')} | project={item.get('project_name')}"
        )


@app.command("show-deadcode")
def show_deadcode(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum candidates to show"),
    compare_ai_soft_edges: bool = typer.Option(False, "--compare-ai-soft-edges", help="Compare hard-only deadcode count against an AI-soft-edge-aware snapshot"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Show top dead-code candidates from the last generated report."""
    paths = resolve_workspace_paths(workspace)
    report_path = os.path.join(paths["reports_dir"], "dead_code_candidates.json")
    if not require_file(report_path, "Dead-code report"):
        return

    payload = load_json_file(report_path)
    candidates = payload.get("candidates", [])[:limit]
    comparison_payload = None
    if compare_ai_soft_edges:
        db_path = paths["db_path"]
        if not require_file(db_path, "Database"):
            return
        ai_soft_path = os.path.join(paths["reports_dir"], "ai_soft_edges.json")
        if require_file(ai_soft_path, "AI soft-edge report"):
            engine, session = open_session(db_path)
            graph_loader = GraphLoader(paths["graphs_dir"])
            graph_loader.load_all()
            hard_snapshot = compute_deadcode_snapshot(session, graph_loader, paths["reports_dir"], include_ai_soft_edges=False)
            graph_loader.load_ai_soft_edges(paths["reports_dir"])
            soft_snapshot = compute_deadcode_snapshot(session, graph_loader, paths["reports_dir"], include_ai_soft_edges=True)
            session.close()
            engine.dispose()
            suppressed = sorted(hard_snapshot["candidate_fqns"] - soft_snapshot["candidate_fqns"])
            comparison_payload = {
                "hard_only_count": hard_snapshot["count"],
                "with_ai_soft_edges_count": soft_snapshot["count"],
                "suppressed_by_ai_soft_edges_count": len(suppressed),
                "suppressed_by_ai_soft_edges": suppressed[:limit],
                "soft_only_suppressions": soft_snapshot["suppressed_by_ai_soft_edges"][:limit],
            }
    if json_output:
        payload_out = {
            "analysis_mode": payload.get("analysis_mode", "unknown"),
            "candidates": candidates,
        }
        if comparison_payload:
            payload_out["comparison"] = comparison_payload
        print(json.dumps(payload_out, indent=2, ensure_ascii=False))
        return

    analysis_mode = payload.get("analysis_mode", "unknown")
    print(f"analysis_mode={analysis_mode}")
    if comparison_payload:
        print(
            "comparison: "
            f"hard_only={comparison_payload['hard_only_count']} | "
            f"with_ai_soft_edges={comparison_payload['with_ai_soft_edges_count']} | "
            f"suppressed_by_ai_soft_edges={comparison_payload['suppressed_by_ai_soft_edges_count']}"
        )
        for item in comparison_payload["soft_only_suppressions"][:5]:
            edge_types = ", ".join(item.get("edge_types", [])[:3])
            print(f"  - soft-suppressed: {item['fqn']} | types={edge_types}")

    for index, item in enumerate(candidates, start=1):
        print(
            f"{index}. [{item.get('kind')}] {item.get('fqn')} | category={item.get('category')} | "
            f"loc={item.get('loc')} | why={item.get('why')}"
        )


@app.command("graph-meta")
def graph_meta(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Show graph metadata such as scan mode and solution path."""
    paths = resolve_workspace_paths(workspace)
    call_graph_path = os.path.join(paths["graphs_dir"], "call_graph.json")
    if not require_file(call_graph_path, "Call graph"):
        return

    payload = load_json_file(call_graph_path)
    graph_meta_payload = payload.get("graph", {})
    print(json.dumps(graph_meta_payload, indent=2, ensure_ascii=False))


def _looks_like_ui_callback_fqn(fqn: str) -> bool:
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


def _read_text_file(path: str) -> str:
    try:
        with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
            return f.read()
    except OSError:
        return ""


def _count_outbound_edge_types(graph, node_ids: list[str], edge_types: set[str]) -> int:
    count = 0
    for node_id in node_ids:
        if node_id not in graph:
            continue
        for _, _, data in graph.out_edges(node_id, data=True):
            if data.get("type") in edge_types:
                count += 1
    return count


def _unique_paths(values: list[str | None]) -> list[str]:
    seen: set[str] = set()
    ordered: list[str] = []
    for value in values:
        if not value:
            continue
        if value in seen:
            continue
        seen.add(value)
        ordered.append(value)
    return ordered


def _select_ai_candidate_symbols(symbols: list[Symbol], kind: str) -> list[Symbol]:
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


def _build_non_xaml_ai_candidates(session, graph_loader: GraphLoader, kind: str, limit: int) -> list[dict]:
    edge_types = {
        "reflection": {"reflection_constructor_dispatch"},
        "di": {"service_provider_dispatch", "autofac_resolve_dispatch", "autofac_module_load"},
    }
    payload: list[dict] = []

    rows = session.query(Document, Project).join(Project, Document.project_id == Project.id).all()
    for document, project_row in rows:
        if getattr(project_row, "is_test_project", 0) == 1:
            continue

        file_path = document.file_path or ""
        if not file_path.lower().endswith(".cs"):
            continue

        text = _read_text_file(file_path)
        if not text:
            continue

        pattern_hits = [marker for marker in AI_CANDIDATE_MARKERS[kind] if marker in text]
        if not pattern_hits:
            continue

        symbols = session.query(Symbol).filter(Symbol.document_id == document.id).all()
        focal_symbols = _select_ai_candidate_symbols(symbols, kind)
        if not focal_symbols:
            continue

        focal_fqns = [symbol.fqn for symbol in focal_symbols]
        hard_edge_count = _count_outbound_edge_types(graph_loader.call_graph, focal_fqns, edge_types[kind])
        isolated_count = sum(1 for symbol in focal_symbols if symbol.fan_in in (None, 0))

        reasons = [
            f"file contains {len(pattern_hits)} {kind} marker(s)",
        ]
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


@app.command("xaml-candidates")
def xaml_candidates(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum XAML candidates to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Suggest XAML/code-behind areas that are good candidates for AI-assisted enrichment."""
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]
    if not require_file(db_path, "Database"):
        return

    engine, session = open_session(db_path)
    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()

    xaml_symbols = session.query(Symbol, Document, Project).outerjoin(Document, Symbol.document_id == Document.id).join(Project, Symbol.project_id == Project.id).filter(Symbol.kind == "xaml").all()
    payload = []

    for xaml_symbol, document, project_row in xaml_symbols:
        xaml_fqn = xaml_symbol.fqn
        outbound_calls = list(graph_loader.call_graph.out_edges(xaml_fqn, data=True)) if xaml_fqn in graph_loader.call_graph else []
        outbound_types = list(graph_loader.type_dependency_graph.out_edges(xaml_fqn, data=True)) if xaml_fqn in graph_loader.type_dependency_graph else []

        event_edges = [edge for edge in outbound_calls if edge[2].get("type") in {"xaml_event", "xaml_action_binding"}]
        command_edges = [edge for edge in outbound_types if edge[2].get("type") in {"xaml_command_binding", "xaml_navigation"}]

        containing_type = xaml_symbol.containing_type or ""
        related_methods = session.query(Symbol, Document).outerjoin(Document, Symbol.document_id == Document.id).filter(
            Symbol.kind == "method",
            Symbol.containing_type == containing_type,
        ).all()

        weak_handlers = []
        for method_symbol, method_document in related_methods:
            if method_symbol.fan_in and method_symbol.fan_in > 0:
                continue
            if not _looks_like_ui_callback_fqn(method_symbol.fqn):
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
        if "\\Shared\\" in file_path or file_path.endswith(".xaml",):
            reasons.append("shared or resource-linked XAML can hide ownership boundaries")
            score += 1

        if score == 0:
            continue

        payload.append({
            "xaml_fqn": xaml_fqn,
            "project": project_row.name,
            "file_path": file_path,
            "containing_type": containing_type,
            "event_edges": len(event_edges),
            "command_edges": len(command_edges),
            "weak_handlers": weak_handlers[:10],
            "reasons": reasons,
            "score": score,
        })

    payload.sort(key=lambda item: (-item["score"], item["xaml_fqn"]))
    payload = payload[:limit]

    if json_output:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
    else:
        for index, item in enumerate(payload, start=1):
            print(f"{index}. {item['xaml_fqn']} | score={item['score']} | project={item['project']}")
            print(f"   file={item['file_path']}")
            print(f"   reasons={'; '.join(item['reasons'])}")
            if item["weak_handlers"]:
                print("   weak_handlers:")
                for handler in item["weak_handlers"][:5]:
                    print(f"     - {handler['fqn']} (loc={handler['loc']})")

    session.close()
    engine.dispose()


@app.command("ai-candidates")
def ai_candidates(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    kind: str = typer.Option("all", "--kind", help="Candidate family: all, xaml, reflection, di"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum candidates to show"),
    bundle_path: str = typer.Option(None, "--bundle-path", help="Write a prompt-ready candidate bundle JSON for external AI review"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Suggest difficult areas where AI soft-edge analysis is likely worth the cost."""
    normalized_kind = kind.lower().strip()
    if normalized_kind not in {"all", "xaml", "reflection", "di"}:
        logger.error("kind must be one of: all, xaml, reflection, di")
        raise typer.Exit(code=1)

    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]
    if not require_file(db_path, "Database"):
        return

    engine, session = open_session(db_path)
    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()

    payload: list[dict] = []
    if normalized_kind in {"all", "xaml"}:
        xaml_symbols = session.query(Symbol, Document, Project).outerjoin(Document, Symbol.document_id == Document.id).join(Project, Symbol.project_id == Project.id).filter(Symbol.kind == "xaml").all()
        xaml_payload = []

        for xaml_symbol, document, project_row in xaml_symbols:
            xaml_fqn = xaml_symbol.fqn
            outbound_calls = list(graph_loader.call_graph.out_edges(xaml_fqn, data=True)) if xaml_fqn in graph_loader.call_graph else []
            outbound_types = list(graph_loader.type_dependency_graph.out_edges(xaml_fqn, data=True)) if xaml_fqn in graph_loader.type_dependency_graph else []

            event_edges = [edge for edge in outbound_calls if edge[2].get("type") in {"xaml_event", "xaml_action_binding"}]
            command_edges = [edge for edge in outbound_types if edge[2].get("type") in {"xaml_command_binding", "xaml_navigation"}]

            containing_type = xaml_symbol.containing_type or ""
            related_methods = session.query(Symbol, Document).outerjoin(Document, Symbol.document_id == Document.id).filter(
                Symbol.kind == "method",
                Symbol.containing_type == containing_type,
            ).all()

            weak_handlers = []
            for method_symbol, method_document in related_methods:
                if method_symbol.fan_in and method_symbol.fan_in > 0:
                    continue
                if not _looks_like_ui_callback_fqn(method_symbol.fqn):
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
            if "\\Shared\\" in file_path or file_path.endswith(".xaml",):
                reasons.append("shared or resource-linked XAML can hide ownership boundaries")
                score += 1

            if score == 0:
                continue

            xaml_payload.append({
                "candidate_kind": "xaml",
                "xaml_fqn": xaml_fqn,
                "project": project_row.name,
                "file_path": file_path,
                "context_files": _unique_paths([file_path] + [handler["file_path"] for handler in weak_handlers[:10]]),
                "containing_type": containing_type,
                "event_edges": len(event_edges),
                "command_edges": len(command_edges),
                "weak_handlers": weak_handlers[:10],
                "reasons": reasons,
                "score": score,
                "suggested_soft_edge_types": AI_CANDIDATE_EDGE_HINTS["xaml"],
                "context_symbols": [xaml_fqn] + [handler["fqn"] for handler in weak_handlers[:5]],
            })

        xaml_payload.sort(key=lambda item: (-item["score"], item["xaml_fqn"]))
        payload.extend(xaml_payload[:limit if normalized_kind == "xaml" else max(limit, 50)])

    if normalized_kind in {"all", "reflection"}:
        payload.extend(_build_non_xaml_ai_candidates(session, graph_loader, "reflection", limit if normalized_kind == "reflection" else max(limit, 50)))

    if normalized_kind in {"all", "di"}:
        payload.extend(_build_non_xaml_ai_candidates(session, graph_loader, "di", limit if normalized_kind == "di" else max(limit, 50)))

    payload.sort(key=lambda item: (-item["score"], item.get("candidate_kind", ""), item.get("file_path", "")))
    payload = payload[:limit]

    bundle_payload = {
        "schema_version": 1,
        "generated_at": utc_timestamp(),
        "workspace": workspace,
        "kind": normalized_kind,
        "notes": (
            "These are AI-escalation candidates. Treat them as soft review targets, not as confirmed graph edges. "
            "Prefer returning ai_soft_edges.json entries only when you can explain the evidence."
        ),
        "candidates": payload,
    }

    if bundle_path:
        os.makedirs(os.path.dirname(os.path.abspath(bundle_path)), exist_ok=True)
        with open(bundle_path, "w", encoding="utf-8") as f:
            json.dump(bundle_payload, f, indent=2, ensure_ascii=False)

    if json_output:
        print(json.dumps(bundle_payload, indent=2, ensure_ascii=False))
    else:
        if bundle_path:
            print(f"bundle={bundle_path}")
        for index, item in enumerate(payload, start=1):
            print(f"{index}. [{item['candidate_kind']}] score={item['score']} | project={item['project']}")
            print(f"   file={item['file_path']}")
            print(f"   suggested_soft_edge_types={', '.join(item.get('suggested_soft_edge_types', []))}")
            print(f"   reasons={'; '.join(item['reasons'])}")
            if item["candidate_kind"] == "xaml":
                print(f"   xaml={item['xaml_fqn']}")
                for handler in item.get("weak_handlers", [])[:5]:
                    print(f"   weak_handler: {handler['fqn']} (loc={handler['loc']})")
            else:
                print(f"   markers={', '.join(item['pattern_hits'])}")
                for symbol in item.get("focal_symbols", [])[:5]:
                    print(f"   focal: {symbol['fqn']} | kind={symbol['kind']} | fan_in={symbol['fan_in']} | loc={symbol['loc']}")
            if item.get("context_files"):
                for context_file in item["context_files"][:3]:
                    print(f"   context_file: {context_file}")

    session.close()
    engine.dispose()

if __name__ == "__main__":
    app()
