import faiss
import json
import os
import numpy as np
from typing import List, Dict, Any, Optional
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

    def search(
        self, 
        query: str, 
        top_k: int = 5, 
        expansion_depth: int = 1, 
        filter_kind: Optional[str] = None,
        filter_project_id: Optional[str] = None
    ) -> List[Dict[str, Any]]:
        if not self.index:
            logger.error("FAISS index is not loaded.")
            return []

        query_vector = self.provider.embed([query])
        faiss.normalize_L2(query_vector)

        sel = None
        if filter_kind or filter_project_id:
            allowed_ids = []
            for vec_id_str, meta in self.metadata.items():
                match_kind = True if not filter_kind else (meta.get("kind") == filter_kind)
                match_project = True if not filter_project_id else (meta.get("project_id") == filter_project_id)
                
                if match_kind and match_project:
                    allowed_ids.append(int(vec_id_str))
            
            if allowed_ids:
                allowed_ids_arr = np.array(allowed_ids, dtype=np.int64)
                sel = faiss.IDSelectorArray(allowed_ids_arr)
            else:
                logger.warning(f"No symbols found matching filter_kind='{filter_kind}' and filter_project_id='{filter_project_id}'")
                return []

        if sel is not None:
            if hasattr(faiss, 'SearchParametersIVF'):
                search_params = faiss.SearchParametersIVF(sel=sel)
            else:
                search_params = faiss.SearchParameters(sel=sel)
            distances, indices = self.index.search(query_vector, top_k, params=search_params)
        else:
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
                    "kind": meta.get("kind", ""),
                    "project_id": meta.get("project_id", ""),
                    "summary": meta["summary"],
                    "context": []
                }
                
                if expansion_depth > 0:
                    context_symbols = self._expand_graph(meta["fqn"], expansion_depth)
                    result["context"] = context_symbols
                    
                results.append(result)

        return results

    def _expand_graph(self, fqn: str, depth: int) -> List[str]:
        context = set()

        def _expand_bidirectional(graph, root: str, limit: int, incoming_prefix: str, outgoing_prefix: str) -> None:
            if root not in graph or limit <= 0:
                return

            incoming_seen = {root}
            outgoing_seen = {root}
            incoming_frontier = {root}
            outgoing_frontier = {root}

            for _ in range(limit):
                next_incoming = set()
                for current in incoming_frontier:
                    for neighbor in graph.predecessors(current):
                        if neighbor in incoming_seen:
                            continue
                        incoming_seen.add(neighbor)
                        next_incoming.add(neighbor)
                        context.add(f"{incoming_prefix}: {neighbor}")

                next_outgoing = set()
                for current in outgoing_frontier:
                    for neighbor in graph.successors(current):
                        if neighbor in outgoing_seen:
                            continue
                        outgoing_seen.add(neighbor)
                        next_outgoing.add(neighbor)
                        context.add(f"{outgoing_prefix}: {neighbor}")

                if not next_incoming and not next_outgoing:
                    break

                incoming_frontier = next_incoming
                outgoing_frontier = next_outgoing

        _expand_bidirectional(self.graph.call_graph, fqn, depth, "Called by", "Calls")
        _expand_bidirectional(self.graph.inheritance_graph, fqn, depth, "Inherited by", "Inherits")

        if hasattr(self.graph, 'type_dependency_graph'):
            _expand_bidirectional(self.graph.type_dependency_graph, fqn, depth, "Used as type by", "Uses type")

        return list(context)
