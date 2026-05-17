import json
import tempfile
import unittest
from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from graph import GraphLoader
from models import Base, Document, Project, Symbol
from related import RelatedFinder


def write_graph(path: Path, graph_type: str, nodes: list[dict], links: list[dict]) -> None:
    path.write_text(
        json.dumps(
            {
                "directed": True,
                "multigraph": False,
                "graph": {"type": graph_type},
                "nodes": nodes,
                "links": links,
            },
            indent=2,
        ),
        encoding="utf-8",
    )


class RelatedFinderTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.workspace = Path(self.temp_dir.name)
        graphs_dir = self.workspace / "output" / "graphs"
        graphs_dir.mkdir(parents=True, exist_ok=True)

        write_graph(
            graphs_dir / "call_graph.json",
            "call",
            [
                {"id": "App.ListUtils.TryGetLast()"},
                {"id": "App.ListUtils.TryGetFirst()"},
                {"id": "App.ListUtils.GetLastOrDefault()"},
                {"id": "App.Controller.UseList()"},
            ],
            [
                {"source": "App.Controller.UseList()", "target": "App.ListUtils.TryGetLast()"},
                {"source": "App.Controller.UseList()", "target": "App.ListUtils.TryGetFirst()"},
            ],
        )
        write_graph(graphs_dir / "inheritance_graph.json", "inheritance", [], [])
        write_graph(graphs_dir / "dependency_graph.json", "dependency", [], [])
        write_graph(
            graphs_dir / "field_access_graph.json",
            "field_access",
            [],
            [],
        )
        write_graph(
            graphs_dir / "type_dependency_graph.json",
            "type_dependency",
            [
                {"id": "App.ListUtils.TryGetLast()"},
                {"id": "App.ListUtils.TryGetFirst()"},
                {"id": "App.ListUtils.GetLastOrDefault()"},
                {"id": "System.Collections.Generic.IReadOnlyList<T>"},
            ],
            [
                {"source": "App.ListUtils.TryGetLast()", "target": "System.Collections.Generic.IReadOnlyList<T>"},
                {"source": "App.ListUtils.TryGetFirst()", "target": "System.Collections.Generic.IReadOnlyList<T>"},
                {"source": "App.ListUtils.GetLastOrDefault()", "target": "System.Collections.Generic.IReadOnlyList<T>"},
            ],
        )

        self.engine = create_engine(f"sqlite:///{self.workspace / 'test.db'}")
        Base.metadata.create_all(self.engine)
        self.session = sessionmaker(bind=self.engine)()
        self.session.add(Project(id="p1", analysis_run_id="run", solution_id="sln", name="App", file_path="App.csproj", project_type="library", is_test_project=0))
        self.session.add(Document(id="d1", project_id="p1", file_path="ListUtils.cs", file_name="ListUtils.cs"))
        self.session.add_all(
            [
                Symbol(id="s1", document_id="d1", project_id="p1", fqn="App.ListUtils.TryGetLast()", name="TryGetLast", kind="method", namespace="App", containing_type="App.ListUtils", accessibility="public", is_static=1, is_abstract=0, is_sealed=0, is_async=0, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=1, line_end=10, loc=10, parameter_count=1, return_type="System.Boolean", has_callback=0, has_ui_dispatch=0, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=0, fan_in=1),
                Symbol(id="s2", document_id="d1", project_id="p1", fqn="App.ListUtils.TryGetFirst()", name="TryGetFirst", kind="method", namespace="App", containing_type="App.ListUtils", accessibility="public", is_static=1, is_abstract=0, is_sealed=0, is_async=0, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=11, line_end=20, loc=10, parameter_count=1, return_type="System.Boolean", has_callback=0, has_ui_dispatch=0, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=0, fan_in=1),
                Symbol(id="s3", document_id="d1", project_id="p1", fqn="App.ListUtils.GetLastOrDefault()", name="GetLastOrDefault", kind="method", namespace="App", containing_type="App.ListUtils", accessibility="public", is_static=1, is_abstract=0, is_sealed=0, is_async=0, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=21, line_end=30, loc=10, parameter_count=1, return_type="T", has_callback=0, has_ui_dispatch=0, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=0, fan_in=0),
            ]
        )
        self.session.commit()

    def tearDown(self) -> None:
        self.session.close()
        self.engine.dispose()
        self.temp_dir.cleanup()

    def test_related_finder_prefers_same_family_methods(self) -> None:
        loader = GraphLoader(str(self.workspace))
        loader.load_all()
        finder = RelatedFinder(self.session, loader)

        source = finder.find_symbol("App.ListUtils.TryGetLast()")
        self.assertIsNotNone(source)

        results = finder.find_related(source, top_k=3)
        ranked_fqns = [result.fqn for result in results]

        self.assertIn("App.ListUtils.TryGetFirst()", ranked_fqns[:2])
        self.assertIn("App.ListUtils.GetLastOrDefault()", ranked_fqns[:3])


if __name__ == "__main__":
    unittest.main()
