import tempfile
import unittest
from pathlib import Path

import networkx as nx

from retrieval import Retriever


class _DummyProvider:
    def embed(self, _texts):
        raise AssertionError("embed should not be called in _expand_graph tests")


class _DummyGraphLoader:
    def __init__(self):
        self.call_graph = nx.DiGraph()
        self.inheritance_graph = nx.DiGraph()
        self.type_dependency_graph = nx.DiGraph()


class RetrieverExpandGraphTests(unittest.TestCase):
    def _build_retriever(self) -> Retriever:
        temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(temp_dir.cleanup)
        loader = _DummyGraphLoader()
        retriever = Retriever(_DummyProvider(), temp_dir.name, loader)
        return retriever

    def test_expand_graph_walks_multiple_hops(self) -> None:
        retriever = self._build_retriever()
        retriever.graph.call_graph.add_edge("A", "B")
        retriever.graph.call_graph.add_edge("B", "C")
        retriever.graph.call_graph.add_edge("D", "A")
        retriever.graph.call_graph.add_edge("E", "D")

        context = retriever._expand_graph("A", 2)

        self.assertIn("Calls: B", context)
        self.assertIn("Calls: C", context)
        self.assertIn("Called by: D", context)
        self.assertIn("Called by: E", context)

    def test_expand_graph_uses_type_dependency_depth(self) -> None:
        retriever = self._build_retriever()
        retriever.graph.type_dependency_graph.add_edge("User", "TypeA")
        retriever.graph.type_dependency_graph.add_edge("TypeA", "TypeB")

        context = retriever._expand_graph("TypeA", 1)
        self.assertIn("Used as type by: User", context)
        self.assertIn("Uses type: TypeB", context)


if __name__ == "__main__":
    unittest.main()
