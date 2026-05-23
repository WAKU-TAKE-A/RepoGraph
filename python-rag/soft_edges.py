import json
from typing import Any


def load_json_file(path: str) -> dict:
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def normalize_ai_soft_edge(edge: dict) -> dict:
    if not isinstance(edge, dict):
        raise ValueError("Each edge item must be an object (dictionary).")

    source = str(edge.get("source", "")).strip()
    target = str(edge.get("target", "")).strip()
    edge_type = str(edge.get("type", "ai_soft_edge")).strip() or "ai_soft_edge"
    if not source or not target:
        raise ValueError("Each AI soft edge needs non-empty source and target.")

    confidence = edge.get("confidence")
    if confidence is not None:
        confidence = float(confidence)
        if confidence < 0.0 or confidence > 1.0:
            raise ValueError("confidence must be between 0.0 and 1.0.")

    evidence = edge.get("evidence", [])
    if evidence is None:
        evidence = []
    if not isinstance(evidence, list):
        raise ValueError("evidence must be a list.")

    notes = edge.get("notes", "")
    return {
        "source": source,
        "target": target,
        "type": edge_type,
        "confidence": confidence,
        "evidence": [str(item) for item in evidence],
        "notes": str(notes) if notes is not None else "",
    }


def load_ai_soft_payload(import_path: str) -> dict:
    payload = load_json_file(import_path)
    raw_edges = payload.get("edges", payload if isinstance(payload, list) else [])
    if not isinstance(raw_edges, list):
        raise ValueError("AI soft edge payload must be a list or an object with an 'edges' list.")

    normalized_edges = [normalize_ai_soft_edge(edge) for edge in raw_edges]
    return {
        "schema_version": payload.get("schema_version", 1) if isinstance(payload, dict) else 1,
        "generated_by": payload.get("generated_by", "external_ai") if isinstance(payload, dict) else "external_ai",
        "notes": payload.get("notes", "") if isinstance(payload, dict) else "",
        "edges": normalized_edges,
    }


def merge_ai_soft_payload(existing_payload: dict, new_payload: dict) -> dict:
    merged: dict[tuple[str, str, str], dict] = {}
    for payload in (existing_payload, new_payload):
        for edge in payload.get("edges", []):
            normalized = normalize_ai_soft_edge(edge)
            merged[(normalized["source"], normalized["target"], normalized["type"])] = normalized

    return {
        "schema_version": max(existing_payload.get("schema_version", 1), new_payload.get("schema_version", 1)),
        "generated_by": "repograph.import_ai_edges",
        "notes": new_payload.get("notes") or existing_payload.get("notes", ""),
        "edges": sorted(
            merged.values(),
            key=lambda item: (item["source"], item["target"], item["type"]),
        ),
    }


def compute_isolation_snapshot(session: Any, graph_loader: Any, reports_dir: str, include_ai_soft_edges: bool, analyzer_cls: Any) -> dict:
    analyzer = analyzer_cls(session, graph_loader, reports_dir, include_ai_soft_edges=include_ai_soft_edges)
    candidates = analyzer.detect_candidates()
    return {
        "count": len(candidates),
        "candidate_fqns": {candidate["fqn"] for candidate in candidates},
        "suppressed_by_ai_soft_edges": getattr(analyzer, "_suppressed_by_ai_soft_edges", []),
    }


def compute_deadcode_snapshot(session: Any, graph_loader: Any, reports_dir: str, include_ai_soft_edges: bool, detector_cls: Any) -> dict:
    return compute_isolation_snapshot(
        session,
        graph_loader,
        reports_dir,
        include_ai_soft_edges=include_ai_soft_edges,
        analyzer_cls=detector_cls,
    )


def summarize_ai_soft_edges(payload_or_edges: dict | list) -> tuple[dict, list[str]]:
    edges = payload_or_edges.get("edges", []) if isinstance(payload_or_edges, dict) else payload_or_edges
    if not isinstance(edges, list):
        edges = []

    summary = {
        "edge_count": len(edges),
        "edge_type_counts": {},
        "confidence": {
            "missing_count": 0,
            "low_count": 0,
            "min": None,
            "max": None,
        },
        "evidence": {
            "missing_or_empty_count": 0,
        },
        "source_target": {
            "missing_source_count": 0,
            "missing_target_count": 0,
        }
    }

    warnings = []
    seen = set()
    confidences = []

    for edge in edges:
        if not isinstance(edge, dict):
            continue
            
        source = str(edge.get("source", "")).strip()
        target = str(edge.get("target", "")).strip()
        edge_type = str(edge.get("type", "")).strip()
        
        if not source:
            summary["source_target"]["missing_source_count"] += 1
        if not target:
            summary["source_target"]["missing_target_count"] += 1
            
        edge_type_key = edge_type or "ai_soft_edge"
        summary["edge_type_counts"][edge_type_key] = summary["edge_type_counts"].get(edge_type_key, 0) + 1

        key = (source, target, edge_type_key)
        if key in seen:
            warnings.append(f"Duplicate edge: {source} -> {target} ({edge_type_key})")
        seen.add(key)

        confidence = edge.get("confidence")
        if confidence is None:
            summary["confidence"]["missing_count"] += 1
            warnings.append(f"Confidence missing for: {source} -> {target}")
        else:
            try:
                c = float(confidence)
                confidences.append(c)
                if c < 0.5:
                    summary["confidence"]["low_count"] += 1
                    warnings.append(f"Low confidence ({c}) for: {source} -> {target}")
            except (ValueError, TypeError):
                summary["confidence"]["missing_count"] += 1

        evidence = edge.get("evidence", [])
        if not evidence:
            summary["evidence"]["missing_or_empty_count"] += 1
            warnings.append(f"Evidence empty for: {source} -> {target}")

    if confidences:
        summary["confidence"]["min"] = min(confidences)
        summary["confidence"]["max"] = max(confidences)

    return summary, warnings

