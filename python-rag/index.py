import numpy as np
import faiss
import json
import os
from typing import List, Dict, Any
from sentence_transformers import SentenceTransformer
from loguru import logger
from summarize import Summarizer
from models import Symbol
from sqlalchemy.orm import Session

class EmbeddingProvider:
    def embed(self, texts: List[str]) -> np.ndarray:
        raise NotImplementedError

class SentenceTransformerProvider(EmbeddingProvider):
    def __init__(self, model_name: str, device: str = "cpu"):
        logger.info(f"Loading embedding model: {model_name} on {device}")
        self.model = SentenceTransformer(model_name, device=device)

    def embed(self, texts: List[str]) -> np.ndarray:
        embeddings = self.model.encode(texts, show_progress_bar=False)
        return np.array(embeddings).astype("float32")

class FaissIndexer:
    def __init__(self, provider: EmbeddingProvider, output_dir: str, index_type: str = "HNSW", hnsw_m: int = 32):
        self.provider = provider
        self.output_dir = output_dir
        self.index_type = index_type
        self.hnsw_m = hnsw_m
        self.index = None
        self.metadata = {}
        os.makedirs(self.output_dir, exist_ok=True)

    def build_index(self, session: Session, summarizer: Summarizer, batch_size: int = 64):
        symbols = session.query(Symbol).all()
        logger.info(f"Building index for {len(symbols)} symbols...")

        all_embeddings = []
        
        for i in range(0, len(symbols), batch_size):
            batch = symbols[i:i+batch_size]
            summaries = []
            
            for symbol in batch:
                summary = summarizer.summarize_symbol(symbol)
                summaries.append(summary)
                # Store metadata for mapping back from FAISS vector ID
                vector_id = len(self.metadata)
                self.metadata[str(vector_id)] = {
                    "id": symbol.id,
                    "fqn": symbol.fqn,
                    "summary": summary,
                    "kind": symbol.kind,
                    "project_id": symbol.project_id
                }
            
            embeddings = self.provider.embed(summaries)
            all_embeddings.append(embeddings)
            logger.info(f"Processed batch {i//batch_size + 1}/{(len(symbols) + batch_size - 1)//batch_size}")

        if all_embeddings:
            final_embeddings = np.vstack(all_embeddings)
            dimension = final_embeddings.shape[1]
            
            # Normalize for inner product (cosine similarity)
            faiss.normalize_L2(final_embeddings)
            
            if self.index_type == "HNSW":
                # HNSW Flat allows efficient approximate nearest neighbor search
                self.index = faiss.IndexHNSWFlat(dimension, self.hnsw_m, faiss.METRIC_INNER_PRODUCT)
            else:
                # Fallback to exhaustive search
                self.index = faiss.IndexFlatIP(dimension)

            self.index.add(final_embeddings)
            logger.info(f"FAISS index built with {self.index.ntotal} vectors of dimension {dimension}")
            self.save()
        else:
            logger.warning("No symbols to index.")

    def save(self):
        index_path = os.path.join(self.output_dir, "faiss.index")
        meta_path = os.path.join(self.output_dir, "metadata.json")
        
        if self.index:
            faiss.write_index(self.index, index_path)
        with open(meta_path, "w", encoding="utf-8") as f:
            json.dump(self.metadata, f, indent=2)
            
        logger.info(f"Saved FAISS index to {index_path} and metadata to {meta_path}")