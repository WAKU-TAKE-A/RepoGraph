import json
import os
from typing import Any, Callable

from graph import GraphLoader
from navigation import (
    resolve_isolation_report_path,
    format_hotspot_rows,
    format_isolation_rows,
    load_json_report,
)
from soft_edges import (
    load_json_file,
    load_ai_soft_payload,
    merge_ai_soft_payload,
    compute_isolation_snapshot,
    summarize_ai_soft_edges,
)


def import_ai_soft_edges(import_path: str, reports_dir: str, require_file: Callable[[str, str], bool], replace: bool) -> dict:
    os.makedirs(reports_dir, exist_ok=True)
    output_path = os.path.join(reports_dir, "ai_soft_edges.json")

    if not require_file(import_path, "AI soft edge input"):
        raise FileNotFoundError(import_path)

    new_payload = load_ai_soft_payload(import_path)
    if not replace and os.path.exists(output_path):
        existing_payload = load_json_file(output_path)
        merged_payload = merge_ai_soft_payload(existing_payload, new_payload)
    else:
        merged_payload = new_payload

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(merged_payload, f, indent=2, ensure_ascii=False)

    return {
        "path": output_path,
        "edge_count": len(merged_payload["edges"]),
        "replace": replace,
    }


def show_ai_soft_edges(reports_dir: str, require_file: Callable[[str, str], bool], limit: int, json_output: bool) -> str:
    report_path = os.path.join(reports_dir, "ai_soft_edges.json")
    if not require_file(report_path, "AI soft edge report"):
        return ""

    payload = load_json_file(report_path)
    edges = payload.get("edges", [])
    quality_summary, quality_warnings = summarize_ai_soft_edges(payload)

    def get_sort_confidence(e: dict) -> float:
        c = e.get("confidence")
        if c is None:
            return 0.0
        try:
            return float(c)
        except (ValueError, TypeError):
            return 0.0

    edges = sorted(edges, key=lambda edge: (-get_sort_confidence(edge), edge.get("source", ""), edge.get("target", "")))[:limit]


    if json_output:
        return json.dumps({
            "quality_summary": quality_summary,
            "quality_warnings": quality_warnings,
            "edges": edges
        }, indent=2, ensure_ascii=False)

    lines: list[str] = []
    if quality_warnings:
        lines.append(f"WARNING: {len(quality_warnings)} quality issues found in AI soft edges.")
        for w in quality_warnings[:3]:
            lines.append(f"  - {w}")
        if len(quality_warnings) > 3:
            lines.append(f"  ... and {len(quality_warnings) - 3} more warnings.")
        lines.append("")

    for edge in edges:
        confidence = edge.get("confidence")
        confidence_text = f"{confidence:.2f}" if isinstance(confidence, float) else "n/a"
        lines.append(f"[{edge.get('type', 'ai_soft_edge')}] confidence={confidence_text} {edge['source']} -> {edge['target']}")
        for evidence in edge.get("evidence", [])[:3]:
            lines.append(f"  - {evidence}")
    return "\n".join(lines)


def show_hotspots_report(reports_dir: str, require_file: Callable[[str, str], bool], limit: int, json_output: bool) -> str:
    report_path = os.path.join(reports_dir, "hotspots.json")
    if not require_file(report_path, "Hotspot report"):
        return ""

    payload = load_json_report(report_path)
    hotspots_payload = payload.get("hotspots", [])[:limit]
    if json_output:
        return json.dumps(
            {
                "navigation_hints": {
                    "usage_notes": "Hotspots indicate structural centers. Use effective_fan_in for prioritization over raw fan_in.",
                    "recommended_commands": [
                        "python-rag/main.py symbols \"<fqn>\"",
                        "python-rag/main.py files"
                    ]
                },
                "schema_version": payload.get("schema_version", 1),
                "report_kind": payload.get("report_kind", "hotspots"),
                "hotspots": hotspots_payload,
                "shared_mutable_state": payload.get("shared_mutable_state", []),
            },
            indent=2,
            ensure_ascii=False,
        )
    return "\n".join(format_hotspot_rows(hotspots_payload))


def show_isolation_report(
    paths: dict[str, str],
    require_file: Callable[[str, str], bool],
    open_session: Callable[[str], tuple[Any, Any]],
    analyzer_cls: Any,
    limit: int,
    compare_ai_soft_edges: bool,
    json_output: bool,
) -> str:
    report_path = resolve_isolation_report_path(paths["reports_dir"])
    if not require_file(report_path, "Structural isolation report"):
        return ""

    payload = load_json_report(report_path)
    candidates = payload.get("candidates", [])[:limit]
    comparison_payload = None

    if compare_ai_soft_edges:
        db_path = paths["db_path"]
        if not require_file(db_path, "Database"):
            return ""
        ai_soft_path = os.path.join(paths["reports_dir"], "ai_soft_edges.json")
        if require_file(ai_soft_path, "AI soft-edge report"):
            engine, session = open_session(db_path)
            graph_loader = GraphLoader(paths["graphs_dir"])
            graph_loader.load_all()
            hard_snapshot = compute_isolation_snapshot(session, graph_loader, paths["reports_dir"], False, analyzer_cls)
            graph_loader.load_ai_soft_edges(paths["reports_dir"])
            soft_snapshot = compute_isolation_snapshot(session, graph_loader, paths["reports_dir"], True, analyzer_cls)
            session.close()
            engine.dispose()
            suppressed = sorted(hard_snapshot["candidate_fqns"] - soft_snapshot["candidate_fqns"])
            comparison_payload = {
                "comparison_kind": "ai_soft_edge_overlay",
                "interpretation": "AI soft edges are optional evidence; they do not prove dead code or modify the hard graph.",
                "hard_only_count": hard_snapshot["count"],
                "with_ai_soft_edges_count": soft_snapshot["count"],
                "soft_covered_by_ai_count": len(suppressed),
                "suppressed_by_ai_soft_edges_count": len(suppressed),
                "soft_covered_by_ai": suppressed[:limit],
                "suppressed_by_ai_soft_edges": suppressed[:limit],
                "soft_only_suppressions": soft_snapshot["suppressed_by_ai_soft_edges"][:limit],
            }

    if json_output:
        payload_out = {
            "navigation_hints": {
                "usage_notes": (
                    "Isolation candidates are NOT a final deadcode verdict. "
                    "They are structural isolation points that serve as investigation entry points. "
                    "Always confirm with graph evidence, text search, and surrounding code."
                ),
                "rule_family_summary": "Shows the distribution of framework rule families involved. Use this as a map of the domain context.",
                "rule_mode_summary": "Shows the mix of HardEdge vs Candidate rules. Candidate rules are unconfirmed heuristics requiring review.",
                "recommended_commands": [
                    "python-rag/main.py rules --json",
                    "python-rag/main.py symbols \"<fqn>\"",
                    "python-rag/main.py related \"<fqn>\"",
                    "python-rag/main.py show-ai-edges"
                ]
            },
            "analysis_mode": payload.get("analysis_mode", "unknown"),
            "report_kind": payload.get("report_kind", "structural_isolation_candidates"),
            "rule_family_summary": payload.get("rule_family_summary", {}),
            "rule_mode_summary": payload.get("rule_mode_summary", {}),
            "candidates": candidates,
        }
        if comparison_payload:
            payload_out["comparison"] = comparison_payload
        return json.dumps(payload_out, indent=2, ensure_ascii=False)

    lines = [f"analysis_mode={payload.get('analysis_mode', 'unknown')}"]
    if comparison_payload:
        lines.append(
            "comparison: "
            f"hard_only={comparison_payload['hard_only_count']} | "
            f"with_ai_soft_edges={comparison_payload['with_ai_soft_edges_count']} | "
            f"soft_covered_by_ai={comparison_payload['suppressed_by_ai_soft_edges_count']}"
        )
        for item in comparison_payload["soft_only_suppressions"][:5]:
            edge_types = ", ".join(item.get("edge_types", [])[:3])
            lines.append(f"  - soft-covered: {item['fqn']} | types={edge_types}")
    lines.extend(format_isolation_rows(candidates))
    return "\n".join(lines)



