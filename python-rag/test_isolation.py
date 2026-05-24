import tempfile
import unittest
from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from isolation import IsolationAnalyzer
from graph import GraphLoader
from models import Base, Document, Project, Symbol


def write_empty_graph(path: Path) -> None:
    path.write_text(
        '{"directed": true, "multigraph": false, "graph": {}, "nodes": [], "links": []}',
        encoding="utf-8",
    )


class IsolationAnalyzerTests(unittest.TestCase):
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

        (graphs_dir / "call_graph.json").write_text(
            """
{
  "directed": true,
  "multigraph": false,
  "graph": {},
  "nodes": [
    { "id": "App.Services.PlainUtility" },
    { "id": "App.Services.DownstreamService.Execute()" }
  ],
  "links": [
    {
      "source": "App.Services.PlainUtility",
      "target": "App.Services.DownstreamService.Execute()",
      "type": "service_provider_dispatch",
      "rule_id": "di.service_configuration",
      "rule_family": "di",
      "rule_mode": "hard"
    }
  ]
}
""".strip(),
            encoding="utf-8",
        )
        (graphs_dir / "type_dependency_graph.json").write_text(
            """
{
  "directed": true,
  "multigraph": false,
  "graph": {},
  "nodes": [
    { "id": "App.Services.PlainUtility" },
    { "id": "App.Services.Dependency" }
  ],
  "links": [
    {
      "source": "App.Services.PlainUtility",
      "target": "App.Services.Dependency",
      "type": "type_usage",
      "rule_id": "xaml.command_binding",
      "rule_family": "xaml",
      "rule_mode": "hard"
    }
  ]
}
""".strip(),
            encoding="utf-8",
        )

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
                Document(id="doc1", project_id="p1", file_path=r"C:\repo\Services\PlainUtility.cs", file_name="PlainUtility.cs"),
                Document(id="doc2", project_id="p1", file_path=r"C:\repo\Views\EditorView.axaml.cs", file_name="EditorView.axaml.cs"),
                Document(id="doc3", project_id="p1", file_path=r"C:\repo\Rendering\WidgetRenderer.cs", file_name="WidgetRenderer.cs"),
                Document(id="doc4", project_id="p1", file_path=r"C:\repo\Uploaders\CustomFileUploader.cs", file_name="CustomFileUploader.cs"),
                Document(id="doc5", project_id="p1", file_path=r"C:\repo\Server\Startup.cs", file_name="Startup.cs"),
                Document(id="doc6", project_id="p1", file_path=r"C:\repo\Server\Middleware\CustomExceptionHandler.cs", file_name="CustomExceptionHandler.cs"),
                Document(id="doc7", project_id="p1", file_path=r"C:\repo\Server\Controllers\IndexerApiController.cs", file_name="IndexerApiController.cs"),
                Document(id="doc8", project_id="p1", file_path=r"C:\repo\Server\Controllers\RequiresIndexer.cs", file_name="RequiresIndexer.cs"),
                Symbol(
                    id="plain",
                    document_id="doc1",
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
                    id="uploader-class",
                    document_id="doc4",
                    project_id="p1",
                    fqn="App.Uploaders.CustomFileUploaderService",
                    name="CustomFileUploaderService",
                    kind="class",
                    namespace="App.Uploaders",
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
                    fan_in=0,
                ),
                Symbol(
                    id="uploader-method",
                    document_id="doc4",
                    project_id="p1",
                    fqn="App.Uploaders.CustomFileUploaderService.CreateUploader()",
                    name="CreateUploader",
                    kind="method",
                    namespace="App.Uploaders",
                    containing_type="App.Uploaders.CustomFileUploaderService",
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
                    line_end=10,
                    loc=10,
                    parameter_count=0,
                    return_type="object",
                    has_callback=0,
                    has_ui_dispatch=0,
                    has_task_spawn=0,
                    has_background_worker=0,
                    has_do_events=0,
                    has_lock=0,
                    has_thread_start=0,
                    has_blocking_wait=0,
                    fan_in=1,
                ),
                Symbol(
                    id="onloaded",
                    document_id="doc2",
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
                    document_id="doc3",
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
                Symbol(
                    id="getter",
                    document_id="doc1",
                    project_id="p1",
                    fqn="App.ViewModels.MainViewModel.ActiveToolName.get",
                    name="get",
                    kind="method",
                    namespace="App.ViewModels",
                    containing_type="App.ViewModels.MainViewModel",
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
                    line_start=41,
                    line_end=44,
                    loc=4,
                    parameter_count=0,
                    return_type="string",
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
                    id="startup-configure",
                    document_id="doc5",
                    project_id="p1",
                    fqn="App.Server.Startup.Configure(IApplicationBuilder, IWebHostEnvironment)",
                    name="Configure",
                    kind="method",
                    namespace="App.Server",
                    containing_type="App.Server.Startup",
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
                    line_end=20,
                    loc=20,
                    parameter_count=2,
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
                    id="middleware-invoke",
                    document_id="doc6",
                    project_id="p1",
                    fqn="App.Server.Middleware.CustomExceptionHandler.Invoke(HttpContext)",
                    name="Invoke",
                    kind="method",
                    namespace="App.Server.Middleware",
                    containing_type="App.Server.Middleware.CustomExceptionHandler",
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
                    line_end=20,
                    loc=20,
                    parameter_count=1,
                    return_type="Task",
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
                    id="controller-action",
                    document_id="doc7",
                    project_id="p1",
                    fqn="App.Server.Controllers.IndexerApiController.Config()",
                    name="Config",
                    kind="method",
                    namespace="App.Server.Controllers",
                    containing_type="App.Server.Controllers.IndexerApiController",
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
                    line_end=20,
                    loc=20,
                    parameter_count=0,
                    return_type="ActionResult",
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
                    id="action-filter",
                    document_id="doc8",
                    project_id="p1",
                    fqn="App.Server.Controllers.RequiresIndexer.OnActionExecuting(ActionExecutingContext)",
                    name="OnActionExecuting",
                    kind="method",
                    namespace="App.Server.Controllers",
                    containing_type="App.Server.Controllers.RequiresIndexer",
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
                    id="receive-1",
                    document_id="doc2",
                    project_id="p1",
                    fqn="App.ViewModels.TerminalViewModel.Receive(SettingsChangedMessage)",
                    name="Receive",
                    kind="method",
                    namespace="App.ViewModels",
                    containing_type="App.ViewModels.TerminalViewModel",
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
                    line_start=21,
                    line_end=24,
                    loc=4,
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
                    id="receive-2",
                    document_id="doc2",
                    project_id="p1",
                    fqn="App.ViewModels.TerminalViewModel.Receive(KeyBindingsChangedMessage)",
                    name="Receive",
                    kind="method",
                    namespace="App.ViewModels",
                    containing_type="App.ViewModels.TerminalViewModel",
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
                    line_start=25,
                    line_end=28,
                    loc=4,
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
                    id="dp-callback",
                    document_id="doc2",
                    project_id="p1",
                    fqn="App.Views.TabBarBackgroundBindingHelper.BindingPathPropertyChanged(DependencyObject, DependencyPropertyChangedEventArgs)",
                    name="BindingPathPropertyChanged",
                    kind="method",
                    namespace="App.Views",
                    containing_type="App.Views.TabBarBackgroundBindingHelper",
                    accessibility="private",
                    is_static=1,
                    is_abstract=0,
                    is_sealed=0,
                    is_async=0,
                    is_partial=0,
                    is_generic=0,
                    is_extension_method=0,
                    is_disposable=0,
                    is_volatile=0,
                    line_start=29,
                    line_end=34,
                    loc=6,
                    parameter_count=2,
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
                    id="explicit-interface",
                    document_id="doc3",
                    project_id="p1",
                    fqn="App.Views.EditorView.App.Runtime.IListener.OnKeyboardCommand(string)",
                    name="OnKeyboardCommand",
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
                    line_start=35,
                    line_end=40,
                    loc=6,
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

    def tearDown(self) -> None:
        self.session.close()
        self.engine.dispose()
        self.temp_dir.cleanup()

    def test_isolation_distinguishes_suppression_from_reason_labels(self) -> None:
        detector = IsolationAnalyzer(self.session, self.graph_loader, str(self.workspace / "output" / "reports"))

        candidates = detector.detect_candidates()
        candidates_by_fqn = {candidate["fqn"]: candidate for candidate in candidates}
        candidate_fqns = set(candidates_by_fqn)

        self.assertIn("App.Services.PlainUtility", candidate_fqns)
        self.assertNotIn("App.Uploaders.CustomFileUploaderService", candidate_fqns)
        self.assertNotIn("App.ViewModels.MainViewModel.ActiveToolName.get", candidate_fqns)
        self.assertNotIn("App.Server.Startup.Configure(IApplicationBuilder, IWebHostEnvironment)", candidate_fqns)
        self.assertNotIn("App.Server.Middleware.CustomExceptionHandler.Invoke(HttpContext)", candidate_fqns)
        self.assertNotIn("App.Server.Controllers.IndexerApiController.Config()", candidate_fqns)
        self.assertNotIn("App.Server.Controllers.RequiresIndexer.OnActionExecuting(ActionExecutingContext)", candidate_fqns)
        self.assertNotIn("App.Views.EditorView.App.Runtime.IListener.OnKeyboardCommand(string)", candidate_fqns)
        self.assertIn("App.Views.EditorView.OnLoaded(RoutedEventArgs)", candidate_fqns)
        self.assertIn("App.Rendering.WidgetRenderer.Render(DrawingContext)", candidate_fqns)
        self.assertIn("App.ViewModels.TerminalViewModel.Receive(SettingsChangedMessage)", candidate_fqns)
        self.assertIn("App.ViewModels.TerminalViewModel.Receive(KeyBindingsChangedMessage)", candidate_fqns)
        self.assertIn("App.Views.TabBarBackgroundBindingHelper.BindingPathPropertyChanged(DependencyObject, DependencyPropertyChangedEventArgs)", candidate_fqns)

        self.assertTrue(any(label.startswith(("xaml.", "ui.")) for label in candidates_by_fqn["App.Views.EditorView.OnLoaded(RoutedEventArgs)"]["reason_labels"]))
        self.assertEqual("xaml.codebehind_callback", candidates_by_fqn["App.Views.EditorView.OnLoaded(RoutedEventArgs)"]["primary_reason_label"])
        self.assertIn("ui.lifecycle_or_render_callback", candidates_by_fqn["App.Rendering.WidgetRenderer.Render(DrawingContext)"]["reason_labels"])
        self.assertEqual("ui.render_callback", candidates_by_fqn["App.Rendering.WidgetRenderer.Render(DrawingContext)"]["primary_reason_label"])
        self.assertIn("mvvm.message_recipient", candidates_by_fqn["App.ViewModels.TerminalViewModel.Receive(SettingsChangedMessage)"]["reason_labels"])
        self.assertEqual("mvvm.message_recipient", candidates_by_fqn["App.ViewModels.TerminalViewModel.Receive(SettingsChangedMessage)"]["primary_reason_label"])
        self.assertTrue(any(label.startswith(("xaml.", "ui.")) for label in candidates_by_fqn["App.Views.TabBarBackgroundBindingHelper.BindingPathPropertyChanged(DependencyObject, DependencyPropertyChangedEventArgs)"]["reason_labels"]))
        self.assertEqual("xaml.dependency_property_callback", candidates_by_fqn["App.Views.TabBarBackgroundBindingHelper.BindingPathPropertyChanged(DependencyObject, DependencyPropertyChangedEventArgs)"]["primary_reason_label"])
        self.assertEqual({"di": 1}, candidates_by_fqn["App.Services.PlainUtility"]["signals"]["outbound_call_rule_families"])
        self.assertEqual({"di.service_configuration": 1}, candidates_by_fqn["App.Services.PlainUtility"]["signals"]["outbound_call_rule_ids"])
        self.assertEqual({"hard": 1}, candidates_by_fqn["App.Services.PlainUtility"]["signals"]["outbound_call_rule_modes"])
        self.assertEqual({"xaml": 1}, candidates_by_fqn["App.Services.PlainUtility"]["signals"]["outbound_type_rule_families"])
        self.assertEqual({"xaml.command_binding": 1}, candidates_by_fqn["App.Services.PlainUtility"]["signals"]["outbound_type_rule_ids"])
        self.assertEqual({"hard": 1}, candidates_by_fqn["App.Services.PlainUtility"]["signals"]["outbound_type_rule_modes"])

    def test_isolation_report_includes_related_existing_implementations_section(self) -> None:
        detector = IsolationAnalyzer(self.session, self.graph_loader, str(self.workspace / "output" / "reports"))

        detector.generate_report()

        report = (self.workspace / "output" / "reports" / "dead_code_candidates.md").read_text(encoding="utf-8")
        json_report = (self.workspace / "output" / "reports" / "dead_code_candidates.json").read_text(encoding="utf-8")
        structural_report = (self.workspace / "output" / "reports" / "structural_isolation_candidates.md").read_text(encoding="utf-8")
        structural_json_report = (self.workspace / "output" / "reports" / "structural_isolation_candidates.json").read_text(encoding="utf-8")

        self.assertIn("# Structural Isolation Candidates", report)
        self.assertIn("## Investigation Categories", report)
        self.assertIn("| Family | Count |", report)
        self.assertIn("## Convention Labels Kept In Candidates", report)
        self.assertIn("| Rank | Category | Primary Label | Labels | Related | LOC | Kind | Why It Looks Isolated | Symbol (FQN) | File |", report)
        self.assertIn("## Why These Candidates Surfaced", report)
        self.assertIn("explanation facts", report)
        self.assertIn("no callers", report)
        self.assertEqual(report, structural_report)
        self.assertIn("\"why\":", json_report)
        self.assertIn("\"signals\":", json_report)
        self.assertIn("\"primary_reason_label\":", json_report)
        self.assertIn("\"reason_labels\":", json_report)
        self.assertIn("\"explanation_facts\":", json_report)
        self.assertIn("\"suppressed_by_family\":", json_report)
        self.assertIn("\"labeled_by_family\":", json_report)
        self.assertIn("\"outbound_call_rule_families\": {", json_report)
        self.assertIn("\"outbound_call_rule_ids\": {", json_report)
        self.assertIn("\"outbound_call_rule_modes\": {", json_report)
        self.assertIn("\"outbound_type_rule_families\": {", json_report)
        self.assertIn("\"outbound_type_rule_ids\": {", json_report)
        self.assertIn("\"outbound_type_rule_modes\": {", json_report)
        self.assertIn("\"rule_family_summary\": {", json_report)
        self.assertIn("\"call_outbound\": {", json_report)
        self.assertIn("\"type_outbound\": {", json_report)
        self.assertIn("\"di\": 1", json_report)
        self.assertIn("\"di.service_configuration\": 1", json_report)
        self.assertIn("\"xaml\": 1", json_report)
        self.assertIn("\"xaml.command_binding\": 1", json_report)
        self.assertIn("\"hard\": 1", json_report)
        self.assertIn("\"report_kind\": \"structural_isolation_candidates\"", structural_json_report)
        self.assertIn("framework context:", report)
        self.assertIn("outbound calls touch di", report)
        self.assertIn("outbound type usage touch xaml", report)



if __name__ == "__main__":
    unittest.main()
