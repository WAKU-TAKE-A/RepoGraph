import json
import tempfile
import unittest
from pathlib import Path

from soft_edges import (
    compute_deadcode_snapshot,
    compute_isolation_snapshot,
    load_ai_soft_payload,
    merge_ai_soft_payload,
    normalize_ai_soft_edge,
)


class DummyAnalyzer:
    def __init__(self, session, graph_loader, reports_dir, include_ai_soft_edges=False):
        self.session = session
        self.graph_loader = graph_loader
        self.reports_dir = reports_dir
        self.include_ai_soft_edges = include_ai_soft_edges
        self._suppressed_by_ai_soft_edges = [{"fqn": "Demo.Legacy.LoadPlugin()"}] if include_ai_soft_edges else []

    def detect_candidates(self):
        if self.include_ai_soft_edges:
            return [{"fqn": "Demo.Kept()"}]
        return [{"fqn": "Demo.Kept()"}, {"fqn": "Demo.Legacy.LoadPlugin()"}]


class SoftEdgesTests(unittest.TestCase):
    def test_normalize_ai_soft_edge_trims_and_validates(self) -> None:
        normalized = normalize_ai_soft_edge(
            {
                "source": "  xaml::Demo.Widget::Widget.xaml ",
                "target": " Demo.Widget.ButtonClick() ",
                "type": " ai_xaml_candidate_event ",
                "confidence": 0.75,
                "evidence": ["match", 123],
                "notes": None,
            }
        )

        self.assertEqual("xaml::Demo.Widget::Widget.xaml", normalized["source"])
        self.assertEqual("Demo.Widget.ButtonClick()", normalized["target"])
        self.assertEqual("ai_xaml_candidate_event", normalized["type"])
        self.assertEqual(0.75, normalized["confidence"])
        self.assertEqual(["match", "123"], normalized["evidence"])
        self.assertEqual("", normalized["notes"])

    def test_load_ai_soft_payload_supports_dict_and_utf8_bom(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            payload_path = Path(temp_dir) / "soft_edges.json"
            payload_path.write_text(
                json.dumps(
                    {
                        "schema_version": 3,
                        "generated_by": "unit-test",
                        "notes": "payload",
                        "edges": [
                            {
                                "source": "Demo.Source()",
                                "target": "Demo.Target()",
                                "type": "ai_reflection_candidate_dispatch",
                                "confidence": 0.4,
                            }
                        ],
                    }
                ),
                encoding="utf-8-sig",
            )

            payload = load_ai_soft_payload(str(payload_path))

        self.assertEqual(3, payload["schema_version"])
        self.assertEqual("unit-test", payload["generated_by"])
        self.assertEqual("payload", payload["notes"])
        self.assertEqual(1, len(payload["edges"]))
        self.assertEqual("Demo.Source()", payload["edges"][0]["source"])

    def test_merge_ai_soft_payload_deduplicates_by_source_target_type(self) -> None:
        existing_payload = {
            "schema_version": 1,
            "notes": "existing",
            "edges": [
                {
                    "source": "Demo.Source()",
                    "target": "Demo.Target()",
                    "type": "ai_xaml_candidate_event",
                    "confidence": 0.2,
                    "evidence": ["old"],
                }
            ],
        }
        new_payload = {
            "schema_version": 2,
            "notes": "new",
            "edges": [
                {
                    "source": "Demo.Source()",
                    "target": "Demo.Target()",
                    "type": "ai_xaml_candidate_event",
                    "confidence": 0.9,
                    "evidence": ["new"],
                },
                {
                    "source": "Demo.Other()",
                    "target": "Demo.Target()",
                    "type": "ai_di_candidate_dispatch",
                    "confidence": 0.8,
                },
            ],
        }

        merged = merge_ai_soft_payload(existing_payload, new_payload)

        self.assertEqual(2, merged["schema_version"])
        self.assertEqual("repograph.import_ai_edges", merged["generated_by"])
        self.assertEqual("new", merged["notes"])
        self.assertEqual(2, len(merged["edges"]))
        self.assertEqual(0.9, merged["edges"][1]["confidence"])

    def test_compute_isolation_snapshot_and_deadcode_snapshot(self) -> None:
        isolation_snapshot = compute_isolation_snapshot(
            session=object(),
            graph_loader=object(),
            reports_dir="reports",
            include_ai_soft_edges=True,
            analyzer_cls=DummyAnalyzer,
        )
        deadcode_snapshot = compute_deadcode_snapshot(
            session=object(),
            graph_loader=object(),
            reports_dir="reports",
            include_ai_soft_edges=False,
            detector_cls=DummyAnalyzer,
        )

        self.assertEqual(1, isolation_snapshot["count"])
        self.assertEqual({"Demo.Kept()"}, isolation_snapshot["candidate_fqns"])
        self.assertEqual([{"fqn": "Demo.Legacy.LoadPlugin()"}], isolation_snapshot["suppressed_by_ai_soft_edges"])
        self.assertEqual(2, deadcode_snapshot["count"])
        self.assertEqual({"Demo.Kept()", "Demo.Legacy.LoadPlugin()"}, deadcode_snapshot["candidate_fqns"])


if __name__ == "__main__":
    unittest.main()
