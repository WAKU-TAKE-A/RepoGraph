import typer
import yaml
import os
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from loguru import logger
from models import Base, Symbol
import networkx as nx
import json

app = typer.Typer()

def load_config():
    with open("rag_config.yml", "r") as f:
        return yaml.safe_load(f)

@app.command()
def doctor():
    """Verify environment and inputs."""
    config = load_config()
    db_path = config["database"]["path"]
    
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
def build_index():
    """Build embedding + FAISS index (Placeholder)."""
    config = load_config()
    db_path = config["database"]["path"]
    
    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    session = Session()
    
    symbols = session.query(Symbol).all()
    logger.info(f"Loaded {len(symbols)} symbols from database.")
    
    # TODO: Implement actual embedding generation and FAISS index construction
    logger.info("Index build completed (Placeholder).")

if __name__ == "__main__":
    app()
