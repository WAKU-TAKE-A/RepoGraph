import typer
import yaml
import os
import json
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from loguru import logger
from graph import GraphLoader
from hotspots import HotspotScorer
from isolation import IsolationAnalyzer
from related import RelatedFinder
from navigation import (
    list_files,
    list_symbols,
    format_file_rows,
    format_symbol_rows,
)
from soft_edges import load_json_file
from ai_candidates import build_xaml_candidates, build_ai_candidates, build_ai_candidate_bundle
from reporting import (
    import_ai_soft_edges,
    show_ai_soft_edges,
    show_hotspots_report,
    show_isolation_report,
)

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

def require_file(path: str, label: str) -> bool:
    if os.path.exists(path):
        return True
    logger.error(f"{label} not found at {path}")
    return False

def load_config():
    config_path = "rag_config.yml"
    if not os.path.exists(config_path):
        return {}
    with open(config_path, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def _run_isolation_report(workspace: str | None, with_ai_soft_edges: bool) -> None:
    paths = resolve_workspace_paths(workspace)
    db_path = paths["db_path"]
    graphs_dir = paths["graphs_dir"]
    reports_dir = paths["reports_dir"]

    engine, session = open_session(db_path)
    graph_loader = GraphLoader(graphs_dir)
    try:
        graph_loader.load_all()
        if with_ai_soft_edges:
            graph_loader.load_ai_soft_edges(reports_dir)
        if not hasattr(graph_loader, "type_dependency_graph"):
            logger.warning("type_dependency_graph not found. Isolation detection will proceed with Call and Inheritance graphs only.")
    except Exception as e:
        logger.error(f"Error loading graphs: {e}")

    analyzer = IsolationAnalyzer(session, graph_loader, reports_dir, include_ai_soft_edges=with_ai_soft_edges)
    analyzer.generate_report()
    session.close()
    engine.dispose()

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
def isolation(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    with_ai_soft_edges: bool = typer.Option(False, "--with-ai-soft-edges", help="Treat imported AI soft edges as additional inbound evidence"),
):
    """Generate structural isolation candidates for follow-up investigation."""
    _run_isolation_report(workspace, with_ai_soft_edges)


@app.command()
def deadcode(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    with_ai_soft_edges: bool = typer.Option(False, "--with-ai-soft-edges", help="Treat imported AI soft edges as additional inbound evidence"),
):
    """Compatibility alias for `isolation`."""
    logger.warning("`deadcode` is a compatibility alias. Prefer `isolation`.")
    _run_isolation_report(workspace, with_ai_soft_edges)

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
    try:
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
    finally:
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
    payload = list_files(session, project, pattern, limit)

    if json_output:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
    else:
        for line in format_file_rows(payload):
            print(line)

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
    payload = list_symbols(session, query_text, kind, project, file_pattern, limit)

    if json_output:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
    else:
        for line in format_symbol_rows(payload):
            print(line)

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
    try:
        result = import_ai_soft_edges(import_path, paths["reports_dir"], require_file, replace)
    except Exception as e:
        logger.error(f"Failed to import AI soft edges: {e}")
        raise typer.Exit(code=1)
    print(json.dumps(result, indent=2, ensure_ascii=False))


@app.command("show-ai-edges")
def show_ai_edges(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum AI soft edges to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Show imported AI soft edges from output/reports/ai_soft_edges.json."""
    paths = resolve_workspace_paths(workspace)
    output = show_ai_soft_edges(paths["reports_dir"], require_file, limit, json_output)
    if output:
        print(output)


@app.command("show-hotspots")
def show_hotspots(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum hotspots to show"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Show top hotspot entries from the last generated report."""
    paths = resolve_workspace_paths(workspace)
    output = show_hotspots_report(paths["reports_dir"], require_file, limit, json_output)
    if output:
        print(output)


@app.command("show-isolation")
def show_isolation(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum candidates to show"),
    compare_ai_soft_edges: bool = typer.Option(False, "--compare-ai-soft-edges", help="Compare hard-only isolation count against an AI-soft-edge-aware snapshot"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Show top structural isolation candidates from the last generated report."""
    paths = resolve_workspace_paths(workspace)
    output = show_isolation_report(
        paths,
        require_file,
        open_session,
        IsolationAnalyzer,
        limit,
        compare_ai_soft_edges,
        json_output,
    )
    if output:
        print(output)


@app.command("show-deadcode")
def show_deadcode(
    workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory"),
    limit: int = typer.Option(20, "--limit", "-n", help="Maximum candidates to show"),
    compare_ai_soft_edges: bool = typer.Option(False, "--compare-ai-soft-edges", help="Compare hard-only isolation count against an AI-soft-edge-aware snapshot"),
    json_output: bool = typer.Option(False, "--json", help="Emit machine-readable JSON"),
):
    """Compatibility alias for `show-isolation`."""
    logger.warning("`show-deadcode` is a compatibility alias. Prefer `show-isolation`.")
    show_isolation(workspace=workspace, limit=limit, compare_ai_soft_edges=compare_ai_soft_edges, json_output=json_output)


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
    payload = build_xaml_candidates(session, graph_loader, limit, include_context=False)

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

    payload = build_ai_candidates(session, graph_loader, normalized_kind, limit)
    bundle_payload = build_ai_candidate_bundle(workspace, normalized_kind, payload)



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
