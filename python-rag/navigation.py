import json
import os

from models import Document, Project, Symbol


def list_files(session, project: str | None, pattern: str | None, limit: int) -> list[dict]:
    query = session.query(Document, Project).join(Project, Document.project_id == Project.id)
    if project:
        project_lower = project.lower()
        query = query.filter((Project.name.ilike(f"%{project_lower}%")) | (Project.id.ilike(f"%{project_lower}%")))
    if pattern:
        query = query.filter((Document.file_path.ilike(f"%{pattern}%")) | (Document.file_name.ilike(f"%{pattern}%")))

    rows = query.order_by(Project.name, Document.file_path).limit(limit).all()
    return [
        {
            "project": project_row.name,
            "project_id": project_row.id,
            "file_name": document.file_name,
            "file_path": document.file_path,
        }
        for document, project_row in rows
    ]


def list_symbols(session, query_text: str, kind: str | None, project: str | None, file_pattern: str | None, limit: int) -> list[dict]:
    symbol_query = session.query(Symbol, Project, Document).join(Project, Symbol.project_id == Project.id).outerjoin(Document, Symbol.document_id == Document.id)
    if query_text:
        symbol_query = symbol_query.filter((Symbol.fqn.ilike(f"%{query_text}%")) | (Symbol.name.ilike(f"%{query_text}%")))
    if kind:
        symbol_query = symbol_query.filter(Symbol.kind == kind)
    if project:
        symbol_query = symbol_query.filter((Project.name.ilike(f"%{project}%")) | (Project.id.ilike(f"%{project}%")))
    if file_pattern:
        symbol_query = symbol_query.filter(Document.file_path.ilike(f"%{file_pattern}%"))

    rows = symbol_query.order_by(Symbol.fan_in.desc().nullslast(), Symbol.loc.desc().nullslast(), Symbol.fqn).limit(limit).all()
    return [
        {
            "fqn": symbol.fqn,
            "name": symbol.name,
            "kind": symbol.kind,
            "project": project_row.name,
            "file_path": document.file_path if document else None,
            "line_start": symbol.line_start,
            "loc": symbol.loc,
            "fan_in": symbol.fan_in,
        }
        for symbol, project_row, document in rows
    ]


def resolve_isolation_report_path(reports_dir: str) -> str:
    structural_path = os.path.join(reports_dir, "structural_isolation_candidates.json")
    if os.path.exists(structural_path):
        return structural_path
    return os.path.join(reports_dir, "dead_code_candidates.json")


def format_file_rows(payload: list[dict]) -> list[str]:
    return [f"[{item['project']}] {item['file_path']}" for item in payload]


def format_symbol_rows(payload: list[dict]) -> list[str]:
    lines = []
    for item in payload:
        location = f"{item['file_path']}:{item['line_start']}" if item["file_path"] and item["line_start"] else item["file_path"] or "(no file)"
        lines.append(f"[{item['kind']}] {item['fqn']} | project={item['project']} | fan_in={item['fan_in']} | loc={item['loc']} | {location}")
    return lines


def format_hotspot_rows(payload: list[dict]) -> list[str]:
    lines = []
    for index, item in enumerate(payload, start=1):
        metrics = item.get("metrics", {})
        lines.append(
            f"{index}. [{item.get('kind')}] {item.get('fqn')} | score={item.get('score')} | "
            f"fan_in={metrics.get('fan_in')} | loc={metrics.get('loc')} | project={item.get('project_name')}"
        )
    return lines


def format_isolation_rows(candidates: list[dict]) -> list[str]:
    lines = []
    for index, item in enumerate(candidates, start=1):
        lines.append(
            f"{index}. [{item.get('kind')}] {item.get('fqn')} | category={item.get('category')} | "
            f"loc={item.get('loc')} | why={item.get('why')}"
        )
    return lines


def load_json_report(path: str) -> dict:
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)
