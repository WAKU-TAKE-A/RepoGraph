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
    with open("rag_config.yml", "r") as f:
        return yaml.safe_load(f)

@app.command()
def doctor(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Verify environment and inputs."""
    config = load_config()
    db_path = os.path.join(workspace, "output", "repository.db") if workspace else config["database"]["path"]
    
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
    db_path = os.path.join(workspace, "output", "repository.db") if workspace else config["database"]["path"]
    graphs_dir = os.path.join(workspace, "output", "graphs") if workspace else config["graphs"]["directory"]
    out_dir = os.path.join(workspace, "output", "embeddings") if workspace else config["output"]["embeddings_dir"]
    model_name = config["embedding"]["model"]
    batch_size = config["embedding"]["batch_size"]
    device = config["embedding"]["device"]
    
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
    indexer = FaissIndexer(provider, out_dir)
    
    # 5. Build Index
    indexer.build_index(session, summarizer, batch_size=batch_size)
    
    logger.info("Index build completed successfully.")

@app.command()
def query(text: str, workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Semantic + graph-aware query."""
    from index import SentenceTransformerProvider
    from retrieval import Retriever

    config = load_config()
    graphs_dir = os.path.join(workspace, "output", "graphs") if workspace else config["graphs"]["directory"]
    out_dir = os.path.join(workspace, "output", "embeddings") if workspace else config["output"]["embeddings_dir"]
    model_name = config["embedding"]["model"]
    device = config["embedding"]["device"]
    top_k = config["retrieval"]["top_k"]
    depth = config["retrieval"]["graph_expansion_depth"]

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()
    
    provider = SentenceTransformerProvider(model_name, device=device)
    retriever = Retriever(provider, out_dir, graph_loader)
    
    results = retriever.search(text, top_k=top_k, expansion_depth=depth)
    
    if not results:
        logger.warning("No results found.")
        return
        
    for i, res in enumerate(results):
        print(f"\n--- Result {i+1} (Score: {res['score']:.4f}) ---")
        print(f"FQN: {res['fqn']}")
        print(f"Summary: {res['summary']}")
        if res['context']:
            print("Graph Context:")
            for ctx in res['context']:
                print(f"  - {ctx}")

@app.command()
def hotspots(workspace: str = typer.Option(None, "--workspace", "-w", help="Override analysis workspace directory")):
    """Compute and display hotspot rankings."""
    config = load_config()
    db_path = os.path.join(workspace, "output", "repository.db") if workspace else config["database"]["path"]
    graphs_dir = os.path.join(workspace, "output", "graphs") if workspace else config["graphs"]["directory"]
    reports_dir = os.path.join(workspace, "output", "reports") if workspace else os.path.join(config["output"]["directory"], "reports")

    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    session = Session()

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()

    scorer = HotspotScorer(session, graph_loader, reports_dir)
    scorer.generate_reports()

if __name__ == "__main__":
    app()
