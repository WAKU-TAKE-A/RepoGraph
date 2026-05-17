import tempfile
import unittest
from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from deadcode import DeadCodeDetector
from graph import GraphLoader
from models import Base, Document, Project, Symbol


def write_empty_graph(path: Path) -> None:
    path.write_text(
        '{"directed": true, "multigraph": false, "graph": {}, "nodes": [], "links": []}',
        encoding="utf-8",
    )


class DeadCodeDetectorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.workspace = Path(self.temp_dir.name)
        graphs_dir = self.workspace / "output" / "graphs"
        graphs_dir.mkdir(parents=True, exist_ok=True)
        for name in (
            "call_graph.json",
            "inheritance_graph.json",
            "dependency_graph.json",
            "field_access_graph.json",
            "type_dependency_graph.json",
        ):
            write_empty_graph(graphs_dir / name)

        self.engine = create_engine(f"sqlite:///{self.workspace / 'test.db'}")
        Base.metadata.create_all(self.engine)
        self.session = sessionmaker(bind=self.engine)()

        self.session.add(
            Project(
                id="p1",
                analysis_run_id="run",
                solution_id="sln",
                name="App",
                file_path="App.csproj",
                project_type="library",
                is_test_project=0,
            )
        )
        self.session.add_all(
            [
                Document(id="doc1", project_id="p1", file_path=r"C:\repo\ImageEffects\GlowImageEffect.cs", file_name="GlowImageEffect.cs"),
                Document(id="doc2", project_id="p1", file_path=r"C:\repo\Services\PlainUtility.cs", file_name="PlainUtility.cs"),
                Document(id="doc3", project_id="p1", file_path=r"C:\repo\Views\EditorView.axaml.cs", file_name="EditorView.axaml.cs"),
                Document(id="doc4", project_id="p1", file_path=r"C:\repo\Services\PlainUtilityHelper.cs", file_name="PlainUtilityHelper.cs"),
                Symbol(
                    id="effect",
                    document_id="doc1",
                    project_id="p1",
                    fqn="App.Effects.GlowImageEffect",
                    name="GlowImageEffect",
                    kind="class",
                    namespace="App.Effects",
                    containing_type=None,
                    accessibility="public",
                    is_static=0,
                    is_abstract=0,
                    is_sealed=0,
                    is_async=0,
                    is_partial=0,
                    is_generic=0,
                    is_extension_method=0,
                    is_disposable=0,
                    is_volatile=0,
                    line_start=1,
                    line_end=100,
                    loc=100,
                    parameter_count=0,
                    return_type=None,
                    has_callback=0,
                    has_ui_dispatch=0,
                    has_task_spawn=0,
                    has_background_worker=0,
                    has_do_events=0,
                    has_lock=0,
                    has_thread_start=0,
                    has_blocking_wait=0,
                    fan_in=0,
                ),
                Symbol(
                    id="plain",
                    document_id="doc2",
                    project_id="p1",
                    fqn="App.Services.PlainUtility",
                    name="PlainUtility",
                    kind="class",
                    namespace="App.Services",
                    containing_type=None,
                    accessibility="public",
                    is_static=0,
                    is_abstract=0,
                    is_sealed=0,
                    is_async=0,
                    is_partial=0,
                    is_generic=0,
                    is_extension_method=0,
                    is_disposable=0,
                    is_volatile=0,
                    line_start=1,
                    line_end=50,
                    loc=50,
                    parameter_count=0,
                    return_type=None,
                    has_callback=0,
                    has_ui_dispatch=0,
                    has_task_spawn=0,
                    has_background_worker=0,
                    has_do_events=0,
                    has_lock=0,
                    has_thread_start=0,
                    has_blocking_wait=0,
                    fan_in=0,
                ),
                Symbol(
                    id="plain-helper",
                    document_id="doc4",
                    project_id="p1",
                    fqn="App.Services.PlainUtilityHelper",
                    name="PlainUtilityHelper",
                    kind="class",
                    namespace="App.Services",
                    containing_type=None,
                    accessibility="public",
                    is_static=0,
                    is_abstract=0,
                    is_sealed=0,
                    is_async=0,
                    is_partial=0,
                    is_generic=0,
                    is_extension_method=0,
                    is_disposable=0,
                    is_volatile=0,
                    line_start=1,
                    line_end=40,
                    loc=40,
                    parameter_count=0,
                    return_type=None,
                    has_callback=0,
                    has_ui_dispatch=0,
                    has_task_spawn=0,
                    has_background_worker=0,
                    has_do_events=0,
                    has_lock=0,
                    has_thread_start=0,
                    has_blocking_wait=0,
                    fan_in=2,
                ),
                Symbol(
                    id="onloaded",
                    document_id="doc3",
                    project_id="p1",
                    fqn="App.Views.EditorView.OnLoaded(RoutedEventArgs)",
                    name="OnLoaded",
                    kind="method",
                    namespace="App.Views",
                    containing_type="App.Views.EditorView",
                    accessibility="private",
                    is_static=0,
                    is_abstract=0,
                    is_sealed=0,
                    is_async=0,
                    is_partial=0,
                    is_generic=0,
                    is_extension_method=0,
                    is_disposable=0,
                    is_volatile=0,
                    line_start=1,
                    line_end=20,
                    loc=20,
                    parameter_count=1,
                    return_type=None,
                    has_callback=0,
                    has_ui_dispatch=0,
                    has_task_spawn=0,
                    has_background_worker=0,
                    has_do_events=0,
                    has_lock=0,
                    has_thread_start=0,
                    has_blocking_wait=0,
                    fan_in=0,
                ),
                Symbol(
                    id="render",
                    document_id="doc2",
                    project_id="p1",
                    fqn="App.Rendering.WidgetRenderer.Render(DrawingContext)",
                    name="Render",
                    kind="method",
                    namespace="App.Rendering",
                    containing_type="App.Rendering.WidgetRenderer",
                    accessibility="public",
                    is_static=0,
                    is_abstract=0,
                    is_sealed=0,
                    is_async=0,
                    is_partial=0,
                    is_generic=0,
                    is_extension_method=0,
                    is_disposable=0,
                    is_volatile=0,
                    line_start=1,
                    line_end=30,
                    loc=30,
                    parameter_count=1,
                    return_type=None,
                    has_callback=0,
                    has_ui_dispatch=0,
                    has_task_spawn=0,
                    has_background_worker=0,
                    has_do_events=0,
                    has_lock=0,
                    has_thread_start=0,
                    has_blocking_wait=0,
                    fan_in=0,
                ),
            ]
        )
        self.session.commit()

        self.graph_loader = GraphLoader(str(self.workspace))
        self.graph_loader.load_all()
        self.graph_loader.inheritance_graph.add_edge(
            "App.Effects.GlowImageEffect",
            "Framework.ImageEffectBase",
            type="extends",
        )

    def tearDown(self) -> None:
        self.session.close()
        self.engine.dispose()
        self.temp_dir.cleanup()

    def test_reflection_discovered_implementations_are_excluded(self) -> None:
        detector = DeadCodeDetector(self.session, self.graph_loader, str(self.workspace / "output" / "reports"))

        candidates = detector.detect_dead_code_candidates()
        candidate_fqns = {candidate["fqn"] for candidate in candidates}

        self.assertNotIn("App.Effects.GlowImageEffect", candidate_fqns)
        self.assertIn("App.Services.PlainUtility", candidate_fqns)
        self.assertNotIn("App.Views.EditorView.OnLoaded(RoutedEventArgs)", candidate_fqns)
        self.assertNotIn("App.Rendering.WidgetRenderer.Render(DrawingContext)", candidate_fqns)

    def test_deadcode_report_includes_related_existing_implementations_section(self) -> None:
        detector = DeadCodeDetector(self.session, self.graph_loader, str(self.workspace / "output" / "reports"))

        detector.generate_report()

        report = (self.workspace / "output" / "reports" / "dead_code_candidates.md").read_text(encoding="utf-8")

        self.assertIn("## Investigation Categories", report)
        self.assertIn("| Rank | Category | Related | LOC | Kind | Symbol (FQN) | File |", report)
        self.assertIn("## Related Existing Implementations", report)
        self.assertIn("App.Services.PlainUtility", report)
        self.assertIn("App.Services.PlainUtilityHelper", report)
        self.assertIn("`near-family`", report)


if __name__ == "__main__":
    unittest.main()
