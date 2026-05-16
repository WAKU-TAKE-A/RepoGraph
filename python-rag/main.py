import typer
import yaml
import os
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from loguru import logger
from models import Base, Symbol
from graph import GraphLoader
from hotspots import HotspotScorer
import networkx as nx
import json

app = typer.Typer()

def load_config():
    config_path = "rag_config.yml"
    if not os.path.exists(config_path):
        return {}
    with open(config_path, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)

@app.command()
def doctor(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Verify environment and inputs."""
    config = load_config()
    db_path = os.path.join(workspace, "output", "repository.db") if workspace else config.get("database", {}).get("path", "")
    
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
    db_path = os.path.join(workspace, "output", "repository.db") if workspace else config.get("database", {}).get("path", "")
    graphs_dir = os.path.join(workspace, "output", "graphs") if workspace else config.get("graphs", {}).get("directory", "")
    out_dir = os.path.join(workspace, "output", "embeddings") if workspace else config.get("output", {}).get("embeddings_dir", "")
    
    model_name = config.get("embedding", {}).get("model", "sentence-transformers/all-MiniLM-L6-v2")
    batch_size = config.get("embedding", {}).get("batch_size", 64)
    device = config.get("embedding", {}).get("device", "cpu")
    
    faiss_config = config.get("faiss", {})
    index_type = faiss_config.get("index_type", "HNSW")
    hnsw_m = faiss_config.get("hnsw_m", 32)
    
    # 1. Load Graphs
    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()
    
    # 2. Init DB session
    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    session = Session()
    
    # 3. Setup Summarizer
    summarizer = Summarizer(graph_loader)
    
    # 4. Setup Embedding Provider & Indexer
    provider = SentenceTransformerProvider(model_name, device=device)
    indexer = FaissIndexer(provider, out_dir, index_type=index_type, hnsw_m=hnsw_m)
    
    # 5. Build Index
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
    graphs_dir = os.path.join(workspace, "output", "graphs") if workspace else config.get("graphs", {}).get("directory", "")
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
    config = load_config()
    db_path = os.path.join(workspace, "output", "repository.db") if workspace else config.get("database", {}).get("path", "")
    graphs_dir = os.path.join(workspace, "output", "graphs") if workspace else config.get("graphs", {}).get("directory", "")
    reports_dir = os.path.join(workspace, "output", "reports") if workspace else os.path.join(config.get("output", {}).get("directory", ""), "reports")

    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    session = Session()

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()

    scorer = HotspotScorer(session, graph_loader, reports_dir)
    scorer.generate_reports()

if __name__ == "__main__":
    app()