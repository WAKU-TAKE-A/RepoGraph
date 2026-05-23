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
    summarize_ai_soft_edges,
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

    def test_normalize_ai_soft_edge_invalid_inputs(self) -> None:
        with self.assertRaisesRegex(ValueError, "Each edge item must be an object"):
            normalize_ai_soft_edge("not a dict")

        with self.assertRaisesRegex(ValueError, "non-empty source and target"):
            normalize_ai_soft_edge({"source": "", "target": "Demo()"})

        with self.assertRaisesRegex(ValueError, "confidence must be between 0.0 and 1.0"):
            normalize_ai_soft_edge({"source": "A", "target": "B", "confidence": 1.5})

        with self.assertRaisesRegex(ValueError, "evidence must be a list"):
            normalize_ai_soft_edge({"source": "A", "target": "B", "evidence": "not a list"})

    def test_load_ai_soft_payload_invalid_edges_list(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            payload_path = Path(temp_dir) / "soft_edges.json"
            payload_path.write_text(json.dumps({"edges": "not a list"}), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "must be a list or an object with an 'edges' list"):
                load_ai_soft_payload(str(payload_path))

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
        self.assertEqual(1, len(isolation_snapshot["suppressed_by_ai_soft_edges"]))
        self.assertEqual("Demo.Legacy.LoadPlugin()", isolation_snapshot["suppressed_by_ai_soft_edges"][0]["fqn"])

    def test_summarize_ai_soft_edges(self) -> None:
        payload = {
            "edges": [
                {"source": "A", "target": "B", "type": "T1", "confidence": 0.9, "evidence": ["E1"]},
                {"source": "C", "target": "D", "type": "T1", "confidence": 0.4, "evidence": []},
                {"source": "E", "target": "F", "type": "T2", "evidence": ["E2"]},
                {"source": "E", "target": "F", "type": "T2", "confidence": "invalid", "evidence": ["E2"]},
                {"source": "", "target": "H", "type": "T3", "confidence": 0.8, "evidence": ["E3"]},
                {"source": "I", "target": "", "type": "T3", "confidence": 0.8, "evidence": ["E3"]},
            ]
        }
        summary, warnings = summarize_ai_soft_edges(payload)

        self.assertEqual(6, summary["edge_count"])
        self.assertEqual({"T1": 2, "T2": 2, "T3": 2}, summary["edge_type_counts"])
        self.assertEqual(2, summary["confidence"]["missing_count"])
        self.assertEqual(1, summary["confidence"]["low_count"])
        self.assertEqual(0.4, summary["confidence"]["min"])
        self.assertEqual(0.9, summary["confidence"]["max"])
        self.assertEqual(1, summary["evidence"]["missing_or_empty_count"])
        self.assertEqual(1, summary["source_target"]["missing_source_count"])
        self.assertEqual(1, summary["source_target"]["missing_target_count"])

        self.assertTrue(any("Duplicate edge: E -> F (T2)" in w for w in warnings))
        self.assertTrue(any("Confidence missing for: E -> F" in w for w in warnings))
        self.assertTrue(any("Low confidence (0.4) for: C -> D" in w for w in warnings))
        self.assertTrue(any("Evidence empty for: C -> D" in w for w in warnings))

        deadcode_snapshot = compute_deadcode_snapshot(
            session=object(),
            graph_loader=object(),
            reports_dir="reports",
            include_ai_soft_edges=False,
            detector_cls=DummyAnalyzer,
        )

        self.assertEqual(2, deadcode_snapshot["count"])
        self.assertEqual({"Demo.Kept()", "Demo.Legacy.LoadPlugin()"}, deadcode_snapshot["candidate_fqns"])


if __name__ == "__main__":
    unittest.main()
