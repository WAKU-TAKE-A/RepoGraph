import faiss
import json
import os
import numpy as np
from typing import List, Dict, Any
from loguru import logger
from index import EmbeddingProvider
from graph import GraphLoader

class Retriever:
    def __init__(self, provider: EmbeddingProvider, index_dir: str, graph_loader: GraphLoader):
        self.provider = provider
        self.index_dir = index_dir
        self.graph = graph_loader
        self.index = None
        self.metadata = {}
        self._load_index()

    def _load_index(self):
        index_path = os.path.join(self.index_dir, "faiss.index")
        meta_path = os.path.join(self.index_dir, "metadata.json")

        if not os.path.exists(index_path) or not os.path.exists(meta_path):
            logger.warning(f"Index or metadata not found in {self.index_dir}. Please run build-index first.")
            return

        self.index = faiss.read_index(index_path)
        with open(meta_path, "r", encoding="utf-8") as f:
            self.metadata = json.load(f)
            
        logger.info(f"Loaded FAISS index with {self.index.ntotal} vectors.")

    def search(self, query: str, top_k: int = 5, expansion_depth: int = 1) -> List[Dict[str, Any]]:
        if not self.index:
            logger.error("FAISS index is not loaded.")
            return []

        # Embed query
        query_vector = self.provider.embed([query])
        faiss.normalize_L2(query_vector)

        # Search FAISS
        distances, indices = self.index.search(query_vector, top_k)
        
        results = []
        for dist, idx in zip(distances[0], indices[0]):
            if idx == -1:
                continue
                
            idx_str = str(idx)
            if idx_str in self.metadata:
                meta = self.metadata[idx_str]
                result = {
                    "score": float(dist),
                    "id": meta["id"],
                    "fqn": meta["fqn"],
                    "summary": meta["summary"],
                    "context": []
                }
                
                # Graph Expansion
                if expansion_depth > 0:
                    context_symbols = self._expand_graph(meta["fqn"], expansion_depth)
                    result["context"] = context_symbols
                    
                results.append(result)

        return results

    def _expand_graph(self, fqn: str, depth: int) -> List[str]:
        context = set()
        
        # Expand via Call Graph
        if fqn in self.graph.call_graph:
            for _ in range(depth):
                # immediate neighbors
                callers = list(self.graph.call_graph.predecessors(fqn))
                callees = list(self.graph.call_graph.successors(fqn))
                for c in callers: context.add(f"Called by: {c}")
                for c in callees: context.add(f"Calls: {c}")

        # Expand via Inheritance Graph
        if fqn in self.graph.inheritance_graph:
            for _ in range(depth):
                bases = list(self.graph.inheritance_graph.successors(fqn))
                derived = list(self.graph.inheritance_graph.predecessors(fqn))
                for b in bases: context.add(f"Inherits: {b}")
                for d in derived: context.add(f"Inherited by: {d}")
                
        return list(context)
