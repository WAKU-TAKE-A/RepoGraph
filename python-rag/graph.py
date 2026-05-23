import networkx as nx
from networkx.readwrite import json_graph
import json
import os
from loguru import logger

LOW_CONFIDENCE_CALL_TYPES = {
    "delegate_reference",
}

FRAMEWORK_CONVENTION_CALL_TYPES = {
    "event_dispatch",
    "lifecycle_entrypoint",
    "serialization_callback",
    "mvvm_toolkit_message_dispatch",
    "autofac_module_load",
    "autofac_reflection_registration",
    "service_provider_dispatch",
    "autofac_resolve_dispatch",
    "reflection_constructor_dispatch",
    "xaml_event",
}

FRAMEWORK_CONVENTION_DEPENDENCY_TYPES = {
    "xaml_command_binding",
    "xaml_navigation",
    "xaml_type_usage",
    "xaml_codebehind",
}

class GraphLoader:
    def __init__(self, graphs_dir: str):
        self.graphs_dir = graphs_dir
        self.call_graph = nx.DiGraph()
        self.inheritance_graph = nx.DiGraph()
        self.dependency_graph = nx.DiGraph()
        self.field_access_graph = nx.DiGraph()
        self.type_dependency_graph = nx.DiGraph()
        self.ai_soft_graph = nx.DiGraph()

    def load_all(self):
        self.call_graph = self._load_graph("call_graph.json")
        self.inheritance_graph = self._load_graph("inheritance_graph.json")
        self.dependency_graph = self._load_graph("dependency_graph.json")
        self.field_access_graph = self._load_graph("field_access_graph.json")
        self.type_dependency_graph = self._load_graph("type_dependency_graph.json")
        logger.info("Graph loading completed.")

    def load_ai_soft_edges(self, reports_dir: str | None = None):
        self.ai_soft_graph = self._load_ai_soft_graph(reports_dir)

    def _load_ai_soft_graph(self, reports_dir: str | None = None) -> nx.DiGraph:
        filepath = self._resolve_ai_soft_path(reports_dir)
        if not filepath:
            logger.info("AI soft edge report not found. Continuing without ai_soft_graph.")
            return nx.DiGraph()

        try:
            with open(filepath, "r", encoding="utf-8") as f:
                payload = json.load(f)
            edges = payload.get("edges", payload if isinstance(payload, list) else [])
            graph = nx.DiGraph()
            for edge in edges:
                source = edge.get("source")
                target = edge.get("target")
                if not source or not target:
                    continue
                graph.add_edge(
                    source,
                    target,
                    type=edge.get("type", "ai_soft_edge"),
                    confidence=edge.get("confidence"),
                    evidence=edge.get("evidence", []),
                    notes=edge.get("notes", ""),
                )
            logger.info(f"Loaded ai_soft_edges.json: {graph.number_of_nodes()} nodes, {graph.number_of_edges()} edges")
            return graph
        except Exception as e:
            logger.error(f"Failed to load AI soft edge file {filepath}: {e}")
            return nx.DiGraph()

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

    def _resolve_ai_soft_path(self, reports_dir: str | None = None) -> str | None:
        candidates: list[str] = []
        if reports_dir:
            candidates.append(os.path.join(reports_dir, "ai_soft_edges.json"))

        candidates.extend([
            os.path.join(os.path.dirname(self.graphs_dir), "reports", "ai_soft_edges.json"),
            os.path.join(self.graphs_dir, "output", "reports", "ai_soft_edges.json"),
            os.path.join(self.graphs_dir, "reports", "ai_soft_edges.json"),
        ])

        for candidate in candidates:
            if os.path.exists(candidate):
                return candidate

        return None

    def get_node_kind(self, graph_name: str, node_id: str) -> str:
        graph = getattr(self, graph_name, None)
        if graph is None or node_id not in graph:
            return ""
        return str(graph.nodes[node_id].get("kind", ""))

    def is_framework_like_call_node(self, node_id: str) -> bool:
        if node_id.startswith("framework::"):
            return True
        kind = self.get_node_kind("call_graph", node_id)
        return kind in {"framework_method", "xaml"}

    def inbound_edge_type_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        graph = getattr(self, graph_name, None)
        if graph is None or node_id not in graph:
            return {}

        counts: dict[str, int] = {}
        for source, _, data in graph.in_edges(node_id, data=True):
            edge_type = str(data.get("type", "unknown"))
            if graph_name == "call_graph" and self.is_framework_like_call_node(source):
                edge_type = f"{edge_type}:framework_source"
            counts[edge_type] = counts.get(edge_type, 0) + 1
        return counts

    def outbound_edge_type_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        graph = getattr(self, graph_name, None)
        if graph is None or node_id not in graph:
            return {}

        counts: dict[str, int] = {}
        for _, target, data in graph.out_edges(node_id, data=True):
            edge_type = str(data.get("type", "unknown"))
            if graph_name == "call_graph" and self.is_framework_like_call_node(target):
                edge_type = f"{edge_type}:framework_target"
            counts[edge_type] = counts.get(edge_type, 0) + 1
        return counts

    def inbound_edge_rule_family_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        return self._edge_attribute_counts(graph_name, node_id, "in", "rule_family")

    def outbound_edge_rule_family_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        return self._edge_attribute_counts(graph_name, node_id, "out", "rule_family")

    def inbound_edge_rule_id_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        return self._edge_attribute_counts(graph_name, node_id, "in", "rule_id")

    def outbound_edge_rule_id_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        return self._edge_attribute_counts(graph_name, node_id, "out", "rule_id")

    def inbound_edge_rule_mode_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        return self._edge_attribute_counts(graph_name, node_id, "in", "rule_mode")

    def outbound_edge_rule_mode_counts(self, graph_name: str, node_id: str) -> dict[str, int]:
        return self._edge_attribute_counts(graph_name, node_id, "out", "rule_mode")

    def _edge_attribute_counts(self, graph_name: str, node_id: str, direction: str, attribute_name: str) -> dict[str, int]:
        graph = getattr(self, graph_name, None)
        if graph is None or node_id not in graph:
            return {}

        counts: dict[str, int] = {}
        if direction == "in":
            edges = graph.in_edges(node_id, data=True)
        else:
            edges = graph.out_edges(node_id, data=True)

        for _source, _target, data in edges:
            attribute_value = str(data.get(attribute_name) or "").strip()
            if not attribute_value:
                continue
            counts[attribute_value] = counts.get(attribute_value, 0) + 1
        return counts

    def weighted_call_degree(self, node_id: str, direction: str) -> tuple[float, dict[str, int]]:
        if node_id not in self.call_graph:
            return 0.0, {}

        if direction == "in":
            edges = self.call_graph.in_edges(node_id, data=True)
            endpoint_index = 0
        else:
            edges = self.call_graph.out_edges(node_id, data=True)
            endpoint_index = 1

        weighted = 0.0
        counts: dict[str, int] = {}
        for source, target, data in edges:
            edge_type = str(data.get("type", "calls"))
            neighbor = source if endpoint_index == 0 else target
            is_framework_neighbor = self.is_framework_like_call_node(neighbor)

            if edge_type in LOW_CONFIDENCE_CALL_TYPES:
                weight = 0.15
            elif edge_type in FRAMEWORK_CONVENTION_CALL_TYPES or is_framework_neighbor:
                weight = 0.35
            else:
                weight = 1.0

            weighted += weight
            counted_type = edge_type
            if is_framework_neighbor:
                suffix = "framework_source" if endpoint_index == 0 else "framework_target"
                counted_type = f"{counted_type}:{suffix}"
            counts[counted_type] = counts.get(counted_type, 0) + 1

        return weighted, counts
