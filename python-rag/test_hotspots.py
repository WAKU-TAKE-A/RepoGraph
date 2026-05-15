import json
import tempfile
import unittest
from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from graph import GraphLoader
from hotspots import HotspotScorer
from models import Base, FieldAccess, Project, Symbol


def write_graph(path: Path, graph_type: str, nodes: list[dict], links: list[dict]) -> None:
    path.write_text(
        json.dumps(
            {
                "directed": True,
                "multigraph": False,
                "graph": {
                    "type": graph_type,
                    "generated_at": "2026-01-01T00:00:00Z",
                    "analysis_run_id": "test-run",
                },
                "nodes": nodes,
                "links": links,
            },
            indent=2,
        ),
        encoding="utf-8",
    )


class HotspotScorerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.workspace = Path(self.temp_dir.name)
        self.graphs_dir = self.workspace / "output" / "graphs"
        self.graphs_dir.mkdir(parents=True, exist_ok=True)
        self.reports_dir = self.workspace / "output" / "reports"
        self.reports_dir.mkdir(parents=True, exist_ok=True)

        write_graph(
            self.graphs_dir / "call_graph.json",
            "call",
            [
                {"id": "App.Editor", "kind": "class"},
                {"id": "App.Editor.Load()", "kind": "method"},
                {"id": "App.Editor.Save()", "kind": "method"},
            ],
            [
                {"source": "App.Editor.Load()", "target": "App.Editor.Save()", "type": "calls", "call_count": 1},
            ],
        )
        write_graph(
            self.graphs_dir / "inheritance_graph.json",
            "inheritance",
            [],
            [],
        )
        write_graph(
            self.graphs_dir / "dependency_graph.json",
            "dependency",
            [
                {"id": "Core", "kind": "project"},
                {"id": "UI", "kind": "project"},
            ],
            [
                {"source": "UI", "target": "Core", "type": "depends_on"},
            ],
        )
        write_graph(
            self.graphs_dir / "field_access_graph.json",
            "field_access",
            [],
            [],
        )

        self.engine = create_engine(f"sqlite:///{self.workspace / 'test.db'}")
        Base.metadata.create_all(self.engine)
        self.session = sessionmaker(bind=self.engine)()

        self.session.add_all(
            [
                Project(id="p-core", analysis_run_id="run", solution_id="sln", name="Core", file_path="Core.csproj", project_type="library", is_test_project=0),
                Project(id="p-ui", analysis_run_id="run", solution_id="sln", name="UI", file_path="UI.csproj", project_type="exe", is_test_project=0),
                Symbol(id="class-editor", document_id=None, project_id="p-ui", fqn="App.Editor", name="Editor", kind="class", namespace="App", containing_type=None, accessibility="public", is_static=0, is_abstract=0, is_sealed=0, is_async=0, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=1, line_end=2000, loc=2000, parameter_count=0, return_type=None, has_callback=0, has_ui_dispatch=0, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=0),
                Symbol(id="load", document_id=None, project_id="p-ui", fqn="App.Editor.Load()", name="Load", kind="method", namespace="App", containing_type="App.Editor", accessibility="private", is_static=0, is_abstract=0, is_sealed=0, is_async=1, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=10, line_end=40, loc=30, parameter_count=0, return_type="System.Threading.Tasks.Task", has_callback=0, has_ui_dispatch=1, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=1),
                Symbol(id="save", document_id=None, project_id="p-ui", fqn="App.Editor.Save()", name="Save", kind="method", namespace="App", containing_type="App.Editor", accessibility="private", is_static=0, is_abstract=0, is_sealed=0, is_async=0, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=41, line_end=60, loc=20, parameter_count=0, return_type="System.Void", has_callback=0, has_ui_dispatch=0, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=0),
                Symbol(id="owned-prop", document_id=None, project_id="p-core", fqn="App.State.Value", name="Value", kind="property", namespace="App", containing_type="App.State", accessibility="public", is_static=0, is_abstract=0, is_sealed=0, is_async=0, is_partial=0, is_generic=0, is_extension_method=0, is_disposable=0, is_volatile=0, line_start=1, line_end=5, loc=5, parameter_count=0, return_type="System.Int32", has_callback=0, has_ui_dispatch=0, has_task_spawn=0, has_background_worker=0, has_do_events=0, has_lock=0, has_thread_start=0, has_blocking_wait=0),
            ]
        )
        self.session.add_all(
            [
                FieldAccess(accessor_fqn="App.Editor.Load()", target_fqn="App.State.Value", access_kind="write", is_external=1),
                FieldAccess(accessor_fqn="App.Editor.Save()", target_fqn="App.State.Value", access_kind="write", is_external=1),
                FieldAccess(accessor_fqn="App.Editor.Load()", target_fqn="System.Windows.UIElement.IsEnabled", access_kind="write", is_external=1),
                FieldAccess(accessor_fqn="App.Editor.Save()", target_fqn="System.Windows.UIElement.IsEnabled", access_kind="write", is_external=1),
            ]
        )
        self.session.commit()

    def tearDown(self) -> None:
        self.session.close()
        self.engine.dispose()
        self.temp_dir.cleanup()

    def test_graph_loader_finds_output_graphs_folder(self) -> None:
        loader = GraphLoader(str(self.workspace))
        loader.load_all()

        self.assertIn("UI", loader.dependency_graph.nodes)
        self.assertIn("Core", loader.dependency_graph.nodes)

    def test_hotspots_use_project_dependency_and_broader_antipattern_rule(self) -> None:
        loader = GraphLoader(str(self.workspace))
        loader.load_all()
        scorer = HotspotScorer(self.session, loader, str(self.reports_dir))

        hotspots = scorer.compute_hotspots()
        editor = next(h for h in hotspots if h["fqn"] == "App.Editor")

        self.assertEqual(editor["metrics"]["static_coupling"], 1)
        self.assertTrue(editor["is_anti_pattern"])

    def test_shared_mutable_state_ignores_framework_properties(self) -> None:
        loader = GraphLoader(str(self.workspace))
        loader.load_all()
        scorer = HotspotScorer(self.session, loader, str(self.reports_dir))

        shared_state = scorer.compute_shared_mutable_state()

        self.assertEqual(len(shared_state), 1)
        self.assertEqual(shared_state[0]["target_fqn"], "App.State.Value")

    def test_threading_hazard_flags_blocking_wait_with_ui_dispatch(self) -> None:
        loader = GraphLoader(str(self.workspace))
        loader.load_all()
        scorer = HotspotScorer(self.session, loader, str(self.reports_dir))

        hotspots = scorer.compute_hotspots()
        load_method = next(h for h in hotspots if h["fqn"] == "App.Editor.Load()")

        self.assertTrue(load_method["is_threading_hazard"])


if __name__ == "__main__":
    unittest.main()
