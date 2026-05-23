import json
import sys
import tempfile
import unittest
from pathlib import Path

from typer.testing import CliRunner
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

sys.path.insert(0, str(Path(__file__).resolve().parent))

from main import app
from models import Base, Document, Project, Symbol


class MainCliTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.workspace = Path(self.temp_dir.name)
        self.output_dir = self.workspace / "output"
        self.graphs_dir = self.output_dir / "graphs"
        self.reports_dir = self.output_dir / "reports"
        self.graphs_dir.mkdir(parents=True, exist_ok=True)
        self.reports_dir.mkdir(parents=True, exist_ok=True)

        self.db_path = self.output_dir / "repository.db"
        self.engine = create_engine(f"sqlite:///{self.db_path}")
        Base.metadata.create_all(self.engine)
        self.session = sessionmaker(bind=self.engine)()

        self.session.add(Project(id="p1", analysis_run_id="run1", solution_id="s1", name="DemoProject", file_path="Demo.csproj", project_type="library", is_test_project=0))
        self.session.add(Document(id="d1", project_id="p1", file_path=r"C:\repo\Demo\Widget.xaml.cs", file_name="Widget.xaml.cs"))
        self.session.add(Symbol(
            id="s1",
            document_id="d1",
            project_id="p1",
            fqn="Demo.Widget.ButtonClick(object, RoutedEventArgs)",
            name="ButtonClick",
            kind="method",
            namespace="Demo",
            containing_type="Demo.Widget",
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
            line_start=42,
            line_end=50,
            loc=8,
            parameter_count=2,
            return_type="System.Void",
            has_callback=0,
            has_ui_dispatch=0,
            has_task_spawn=0,
            has_background_worker=0,
            has_do_events=0,
            has_lock=0,
            has_thread_start=0,
            has_blocking_wait=0,
            fan_in=3,
        ))
        self.session.add(Document(id="d2", project_id="p1", file_path=r"C:\repo\Demo\Widget.xaml", file_name="Widget.xaml"))
        self.session.add(Symbol(
            id="x1",
            document_id="d2",
            project_id="p1",
            fqn="xaml::Demo.Widget::Widget.xaml",
            name="Demo.Widget",
            kind="xaml",
            namespace="Demo",
            containing_type="Demo.Widget",
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
        ))
        self.session.add(Symbol(
            id="s2",
            document_id="d1",
            project_id="p1",
            fqn="Demo.Widget.OnLoaded(object, RoutedEventArgs)",
            name="OnLoaded",
            kind="method",
            namespace="Demo",
            containing_type="Demo.Widget",
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
            line_start=60,
            line_end=68,
            loc=8,
            parameter_count=2,
            return_type="System.Void",
            has_callback=0,
            has_ui_dispatch=0,
            has_task_spawn=0,
            has_background_worker=0,
            has_do_events=0,
            has_lock=0,
            has_thread_start=0,
            has_blocking_wait=0,
            fan_in=0,
        ))
        self.session.commit()

        (self.graphs_dir / "call_graph.json").write_text(
            json.dumps(
                {
                    "directed": True,
                    "multigraph": False,
                    "graph": {
                        "type": "call",
                        "scan_mode": "full",
                        "solution_path": r"C:\repo\Demo.sln",
                    },
                    "nodes": [],
                    "links": [],
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        (self.reports_dir / "hotspots.json").write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "report_kind": "hotspots",
                    "hotspots": [
                        {
                            "fqn": "Demo.Widget",
                            "kind": "class",
                            "score": 0.9,
                            "project_name": "DemoProject",
                            "metrics": {"fan_in": 10, "loc": 120},
                        }
                    ],
                    "shared_mutable_state": [],
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        (self.reports_dir / "structural_isolation_candidates.json").write_text(
            json.dumps(
                {
                    "analysis_mode": "hard-only",
                    "report_kind": "structural_isolation_candidates",
                    "rule_family_summary": {
                        "call_inbound": {},
                        "call_outbound": {"di": 2},
                        "type_inbound": {},
                        "type_outbound": {"xaml": 1},
                    },
                    "rule_mode_summary": {
                        "call_inbound": {},
                        "call_outbound": {"hardedge": 2},
                        "type_inbound": {},
                        "type_outbound": {"hardedge": 1},
                    },
                    "candidates": [
                        {
                            "fqn": "Demo.Legacy.Unused()",
                            "kind": "method",
                            "category": "isolated",
                            "loc": 20,
                            "why": "no callers; no type users; framework context: outbound calls touch di",
                        }
                    ]
                },
                indent=2,
            ),
            encoding="utf-8",
        )

        self.runner = CliRunner()

    def tearDown(self) -> None:
        self.session.close()
        self.engine.dispose()
        self.temp_dir.cleanup()

    def test_files_lists_documents_from_workspace_database(self) -> None:
        result = self.runner.invoke(app, ["files", "--workspace", str(self.workspace)])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("Widget.xaml.cs", result.stdout)
        self.assertIn("DemoProject", result.stdout)

    def test_symbols_lists_matching_symbols(self) -> None:
        result = self.runner.invoke(app, ["symbols", "--workspace", str(self.workspace), "ButtonClick"])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("Demo.Widget.ButtonClick", result.stdout)
        self.assertIn("fan_in=3", result.stdout)

    def test_show_hotspots_reads_json_report(self) -> None:
        result = self.runner.invoke(app, ["show-hotspots", "--workspace", str(self.workspace)])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("Demo.Widget", result.stdout)
        self.assertIn("score=0.9", result.stdout)

    def test_show_hotspots_json_returns_wrapped_payload(self) -> None:
        result = self.runner.invoke(app, ["show-hotspots", "--workspace", str(self.workspace), "--json"])
        self.assertEqual(result.exit_code, 0)
        payload = json.loads(result.stdout)
        self.assertIn("navigation_hints", payload)
        self.assertIn("effective_fan_in", payload["navigation_hints"]["usage_notes"])
        self.assertIn("python-rag/main.py symbols \"<fqn>\"", payload["navigation_hints"]["recommended_commands"])
        self.assertEqual(payload["schema_version"], 1)
        self.assertEqual(payload["report_kind"], "hotspots")
        self.assertEqual(len(payload["hotspots"]), 1)
        self.assertEqual(payload["shared_mutable_state"], [])

    def test_show_deadcode_reads_json_report(self) -> None:
        result = self.runner.invoke(app, ["show-deadcode", "--workspace", str(self.workspace)])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("Demo.Legacy.Unused()", result.stdout)
        self.assertIn("no callers; no type users", result.stdout)

    def test_show_isolation_reads_json_report(self) -> None:
        result = self.runner.invoke(app, ["show-isolation", "--workspace", str(self.workspace)])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("Demo.Legacy.Unused()", result.stdout)
        self.assertIn("analysis_mode=hard-only", result.stdout)

    def test_show_isolation_json_includes_rule_family_summary(self) -> None:
        result = self.runner.invoke(app, ["show-isolation", "--workspace", str(self.workspace), "--json"])
        self.assertEqual(result.exit_code, 0)
        payload = json.loads(result.stdout)
        self.assertIn("navigation_hints", payload)
        self.assertIn("NOT a final deadcode verdict", payload["navigation_hints"]["usage_notes"])
        self.assertIn("rule_family_summary", payload["navigation_hints"])
        self.assertIn("rule_mode_summary", payload["navigation_hints"])
        self.assertIn("python-rag/main.py rules --json", payload["navigation_hints"]["recommended_commands"])
        self.assertEqual(payload["report_kind"], "structural_isolation_candidates")
        self.assertEqual(payload["rule_family_summary"]["call_outbound"]["di"], 2)
        self.assertEqual(payload["rule_family_summary"]["type_outbound"]["xaml"], 1)
        self.assertEqual(payload["rule_mode_summary"]["call_outbound"]["hardedge"], 2)

    def test_graph_meta_reads_scan_metadata(self) -> None:
        result = self.runner.invoke(app, ["graph-meta", "--workspace", str(self.workspace)])
        self.assertEqual(result.exit_code, 0)
        self.assertIn('"scan_mode": "full"', result.stdout)
        self.assertIn('"solution_path": "C:\\\\repo\\\\Demo.sln"', result.stdout)

    def test_xaml_candidates_surfaces_unrecovered_codebehind(self) -> None:
        result = self.runner.invoke(app, ["xaml-candidates", "--workspace", str(self.workspace)])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("xaml::Demo.Widget::Widget.xaml", result.stdout)
        self.assertIn("Demo.Widget.OnLoaded(object, RoutedEventArgs)", result.stdout)

    def test_ai_candidates_kind_xaml_reuses_xaml_candidate_logic(self) -> None:
        result = self.runner.invoke(app, ["ai-candidates", "--workspace", str(self.workspace), "--kind", "xaml"])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("xaml::Demo.Widget::Widget.xaml", result.stdout)
        self.assertIn("Demo.Widget.OnLoaded(object, RoutedEventArgs)", result.stdout)

    def test_import_ai_edges_and_show_ai_edges(self) -> None:
        import_path = self.workspace / "soft_edges.json"
        import_path.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "generated_by": "test",
                    "edges": [
                        {
                            "source": "xaml::Demo.Widget::Widget.xaml",
                            "target": "Demo.Widget.ButtonClick(object, RoutedEventArgs)",
                            "type": "ai_xaml_candidate_event",
                            "confidence": 0.82,
                            "evidence": ["matching x:Class", "runtime AddHandler"],
                        }
                    ],
                },
                indent=2,
            ),
            encoding="utf-8",
        )

        import_result = self.runner.invoke(app, ["import-ai-edges", str(import_path), "--workspace", str(self.workspace)])
        self.assertEqual(import_result.exit_code, 0)
        self.assertIn('"edge_count": 1', import_result.stdout)

        show_result = self.runner.invoke(app, ["show-ai-edges", "--workspace", str(self.workspace)])
        self.assertEqual(show_result.exit_code, 0)
        self.assertIn("ai_xaml_candidate_event", show_result.stdout)
        self.assertIn("Demo.Widget.ButtonClick(object, RoutedEventArgs)", show_result.stdout)

    def test_show_deadcode_compare_ai_soft_edges_reports_difference(self) -> None:
        self.session.add(Symbol(
            id="s3-soft",
            document_id="d1",
            project_id="p1",
            fqn="Demo.Legacy.LoadPlugin()",
            name="LoadPlugin",
            kind="method",
            namespace="Demo",
            containing_type="Demo.Legacy",
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
            line_start=80,
            line_end=88,
            loc=8,
            parameter_count=0,
            return_type="System.Void",
            has_callback=0,
            has_ui_dispatch=0,
            has_task_spawn=0,
            has_background_worker=0,
            has_do_events=0,
            has_lock=0,
            has_thread_start=0,
            has_blocking_wait=0,
            fan_in=0,
        ))
        self.session.commit()

        import_path = self.workspace / "soft_edges.json"
        import_path.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "generated_by": "test",
                    "edges": [
                        {
                            "source": "xaml::Demo.Widget::Widget.xaml",
                            "target": "Demo.Legacy.LoadPlugin()",
                            "type": "ai_xaml_candidate_event",
                            "confidence": 0.91,
                            "evidence": ["matching x:Class", "runtime hookup"],
                        }
                    ],
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        import_result = self.runner.invoke(app, ["import-ai-edges", str(import_path), "--workspace", str(self.workspace)])
        self.assertEqual(import_result.exit_code, 0)

        compare_result = self.runner.invoke(app, ["show-deadcode", "--workspace", str(self.workspace), "--compare-ai-soft-edges"])
        self.assertEqual(compare_result.exit_code, 0)
        self.assertIn("comparison:", compare_result.stdout)
        self.assertIn("suppressed_by_ai_soft_edges=1", compare_result.stdout)
        self.assertIn("Demo.Legacy.LoadPlugin()", compare_result.stdout)

    def test_ai_candidates_reflection_surfaces_loader_file(self) -> None:
        source_path = self.workspace / "PluginLoader.cs"
        source_path.write_text(
            "\n".join(
                [
                    "using System;",
                    "using System.Reflection;",
                    "class PluginLoader {",
                    "  object Load(Type t) {",
                    "    return Activator.CreateInstance(t);",
                    "  }",
                    "}",
                ]
            ),
            encoding="utf-8",
        )

        self.session.add(Document(id="d3", project_id="p1", file_path=str(source_path), file_name="PluginLoader.cs"))
        self.session.add(Symbol(
            id="s3",
            document_id="d3",
            project_id="p1",
            fqn="Demo.PluginLoader.Load(System.Type)",
            name="Load",
            kind="method",
            namespace="Demo",
            containing_type="Demo.PluginLoader",
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
            line_start=4,
            line_end=5,
            loc=2,
            parameter_count=1,
            return_type="System.Object",
            has_callback=0,
            has_ui_dispatch=0,
            has_task_spawn=0,
            has_background_worker=0,
            has_do_events=0,
            has_lock=0,
            has_thread_start=0,
            has_blocking_wait=0,
            fan_in=0,
        ))
        self.session.commit()

        result = self.runner.invoke(app, ["ai-candidates", "--workspace", str(self.workspace), "--kind", "reflection", "--limit", "5"])
        self.assertEqual(result.exit_code, 0)
        self.assertIn("[reflection]", result.stdout)
        self.assertIn("PluginLoader.cs", result.stdout)
        self.assertIn("Activator.CreateInstance", result.stdout)

    def test_ai_candidates_bundle_path_writes_prompt_ready_json(self) -> None:
        source_path = self.workspace / "PluginLoader.cs"
        source_path.write_text(
            "\n".join(
                [
                    "using System;",
                    "using System.Reflection;",
                    "class PluginLoader {",
                    "  object Load(Type t) {",
                    "    return Activator.CreateInstance(t);",
                    "  }",
                    "}",
                ]
            ),
            encoding="utf-8",
        )

        self.session.add(Document(id="d4", project_id="p1", file_path=str(source_path), file_name="PluginLoader.cs"))
        self.session.add(Symbol(
            id="s4",
            document_id="d4",
            project_id="p1",
            fqn="Demo.PluginLoader.Load(System.Type)",
            name="Load",
            kind="method",
            namespace="Demo",
            containing_type="Demo.PluginLoader",
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
            line_start=4,
            line_end=5,
            loc=2,
            parameter_count=1,
            return_type="System.Object",
            has_callback=0,
            has_ui_dispatch=0,
            has_task_spawn=0,
            has_background_worker=0,
            has_do_events=0,
            has_lock=0,
            has_thread_start=0,
            has_blocking_wait=0,
            fan_in=0,
        ))
        self.session.commit()

        bundle_path = self.workspace / "reflection_bundle.json"
        result = self.runner.invoke(
            app,
            ["ai-candidates", "--workspace", str(self.workspace), "--kind", "reflection", "--limit", "5", "--bundle-path", str(bundle_path)],
        )
        self.assertEqual(result.exit_code, 0)
        self.assertTrue(bundle_path.exists())
        payload = json.loads(bundle_path.read_text(encoding="utf-8"))
        self.assertEqual(payload["bundle_schema_version"], 1.1)
        self.assertEqual(payload["kind"], "reflection")
        self.assertIn("soft review targets", payload["usage_notes"])
        self.assertEqual("Confirmed by strict C# parsing rules.", payload["rule_mode_legend"]["HardEdge"])
        self.assertIn("python-rag/main.py rules --json", payload["recommended_commands"])
        self.assertTrue(payload["candidates"])
        self.assertIn("suggested_soft_edge_types", payload["candidates"][0])


if __name__ == "__main__":
    unittest.main()
