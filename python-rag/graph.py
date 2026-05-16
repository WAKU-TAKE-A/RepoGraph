import networkx as nx
from networkx.readwrite import json_graph
import json
import os
from loguru import logger

class GraphLoader:
    def __init__(self, graphs_dir: str):
        self.graphs_dir = graphs_dir
        self.call_graph = nx.DiGraph()
        self.inheritance_graph = nx.DiGraph()
        self.dependency_graph = nx.DiGraph()
        self.field_access_graph = nx.DiGraph()
        self.type_dependency_graph = nx.DiGraph()

    def load_all(self):
        self.call_graph = self._load_graph("call_graph.json")
        self.inheritance_graph = self._load_graph("inheritance_graph.json")
        self.dependency_graph = self._load_graph("dependency_graph.json")
        self.field_access_graph = self._load_graph("field_access_graph.json")
        self.type_dependency_graph = self._load_graph("type_dependency_graph.json")
        logger.info("Graph loading completed.")

    def _load_graph(self, filename: str) -> nx.DiGraph:
        filepath = self._resolve_graph_path(filename)
        if not filepath:
            logger.warning(f"Graph file not found: {filename} under {self.graphs_dir}. Returning empty graph.")
            return nx.DiGraph()

        try:
            with open(filepath, "r", encoding="utf-8") as f:
                data = json.load(f)
            
            if 'links' in data and 'edges' not in data:
                data['edges'] = data.pop('links')
                
            G = json_graph.node_link_graph(data)
            logger.info(f"Loaded {filename}: {G.number_of_nodes()} nodes, {G.number_of_edges()} edges")
            return G
        except Exception as e:
            logger.error(f"Failed to load {filepath}: {e}")
            return nx.DiGraph()

    def _resolve_graph_path(self, filename: str) -> str | None:
        candidates = [
            os.path.join(self.graphs_dir, filename),
            os.path.join(self.graphs_dir, "output", "graphs", filename),
            os.path.join(self.graphs_dir, "graphs", filename),
        ]

        for candidate in candidates:
            if os.path.exists(candidate):
                return candidate

        return None
