import re
from dataclasses import dataclass
from difflib import SequenceMatcher
from typing import Dict, Iterable, List, Optional, Set

from sqlalchemy.orm import Session

from graph import GraphLoader
from models import Symbol


TOKEN_SPLIT_PATTERN = re.compile(r"(?<!^)(?=[A-Z])|[_\W]+")


@dataclass
class RelatedCandidate:
    fqn: str
    kind: str
    score: float
    reasons: List[str]

    def to_dict(self) -> Dict[str, object]:
        return {
            "fqn": self.fqn,
            "kind": self.kind,
            "score": self.score,
            "reasons": self.reasons,
        }


class RelatedFinder:
    def __init__(self, session: Session, graph_loader: GraphLoader):
        self.session = session
        self.graph = graph_loader
        self._symbols = self.session.query(Symbol).all()
        self._symbol_by_fqn: Dict[str, Symbol] = {symbol.fqn: symbol for symbol in self._symbols}

    def find_symbol(self, symbol_query: str) -> Optional[Symbol]:
        if symbol_query in self._symbol_by_fqn:
            return self._symbol_by_fqn[symbol_query]

        exact_name_matches = [
            symbol for symbol in self._symbols
            if symbol.name and symbol.name.lower() == symbol_query.lower()
        ]
        if len(exact_name_matches) == 1:
            return exact_name_matches[0]

        contains_matches = [
            symbol for symbol in self._symbols
            if symbol_query.lower() in symbol.fqn.lower()
        ]
        if len(contains_matches) == 1:
            return contains_matches[0]

        return None

    def suggest_matches(self, symbol_query: str, limit: int = 10) -> List[str]:
        scored = []
        query_lower = symbol_query.lower()
        for symbol in self._symbols:
            ratio = SequenceMatcher(None, query_lower, symbol.fqn.lower()).ratio()
            if query_lower in symbol.fqn.lower():
                ratio += 0.2
            scored.append((ratio, symbol.fqn))

        scored.sort(reverse=True)
        return [fqn for _, fqn in scored[:limit]]

    def find_related(self, source_symbol: Symbol, top_k: int = 10, same_kind_only: bool = True) -> List[RelatedCandidate]:
        source_tokens = _tokenize_symbol(source_symbol.fqn)
        source_graph = self._collect_graph_features(source_symbol.fqn)

        candidates: List[RelatedCandidate] = []
        for candidate in self._symbols:
            if candidate.fqn == source_symbol.fqn:
                continue
            if same_kind_only and candidate.kind != source_symbol.kind:
                continue
            if candidate.kind in {"xaml", "lambda", "framework_method"}:
                continue

            score = 0.0
            reasons: List[str] = []

            if candidate.kind == source_symbol.kind:
                score += 0.2
                reasons.append("same kind")

            candidate_tokens = _tokenize_symbol(candidate.fqn)
            token_overlap = _jaccard(source_tokens, candidate_tokens)
            if token_overlap > 0:
                score += token_overlap * 0.35
                reasons.append(f"name tokens {token_overlap:.2f}")

            name_similarity = SequenceMatcher(None, source_symbol.name or "", candidate.name or "").ratio()
            if name_similarity > 0.55:
                score += name_similarity * 0.15
                reasons.append(f"name shape {name_similarity:.2f}")

            if source_symbol.containing_type and candidate.containing_type == source_symbol.containing_type:
                score += 0.2
                reasons.append("same containing type")

            if source_symbol.namespace and candidate.namespace == source_symbol.namespace:
                score += 0.1
                reasons.append("same namespace")

            if source_symbol.project_id and candidate.project_id == source_symbol.project_id:
                score += 0.08
                reasons.append("same project")

            if source_symbol.document and candidate.document:
                source_file = source_symbol.document.file_name or ""
                candidate_file = candidate.document.file_name or ""
                if source_file and candidate_file and source_file == candidate_file:
                    score += 0.08
                    reasons.append("same file")
                elif source_file and candidate_file:
                    source_stem = source_file.rsplit(".", 1)[0]
                    candidate_stem = candidate_file.rsplit(".", 1)[0]
                    file_shape = SequenceMatcher(None, source_stem.lower(), candidate_stem.lower()).ratio()
                    if file_shape >= 0.65:
                        score += file_shape * 0.05
                        reasons.append(f"file family {file_shape:.2f}")

            if source_symbol.return_type and candidate.return_type == source_symbol.return_type:
                score += 0.05
                reasons.append("same return type")

            if source_symbol.parameter_count is not None and candidate.parameter_count == source_symbol.parameter_count:
                score += 0.05
                reasons.append("same parameter count")

            candidate_graph = self._collect_graph_features(candidate.fqn)
            graph_score, graph_reasons = _score_graph_similarity(source_graph, candidate_graph)
            if graph_score > 0:
                score += graph_score
                reasons.extend(graph_reasons)

            if score <= 0:
                continue

            candidates.append(
                RelatedCandidate(
                    fqn=candidate.fqn,
                    kind=candidate.kind,
                    score=round(score, 4),
                    reasons=reasons,
                )
            )

        candidates.sort(key=lambda item: item.score, reverse=True)
        return candidates[:top_k]

    def _collect_graph_features(self, fqn: str) -> Dict[str, Set[str]]:
        return {
            "callers": self._predecessors(self.graph.call_graph, fqn),
            "callees": self._successors(self.graph.call_graph, fqn),
            "bases": self._successors(self.graph.inheritance_graph, fqn),
            "derived": self._predecessors(self.graph.inheritance_graph, fqn),
            "type_users": self._predecessors(self.graph.type_dependency_graph, fqn),
            "used_types": self._successors(self.graph.type_dependency_graph, fqn),
            "field_users": self._predecessors(self.graph.field_access_graph, fqn),
            "used_fields": self._successors(self.graph.field_access_graph, fqn),
        }

    @staticmethod
    def _predecessors(graph, fqn: str) -> Set[str]:
        if fqn not in graph:
            return set()
        return set(graph.predecessors(fqn))

    @staticmethod
    def _successors(graph, fqn: str) -> Set[str]:
        if fqn not in graph:
            return set()
        return set(graph.successors(fqn))


def _tokenize_symbol(value: str) -> Set[str]:
    normalized = value.replace("::", ".")
    parts: List[str] = []
    for piece in normalized.split("."):
        parts.extend(token for token in TOKEN_SPLIT_PATTERN.split(piece) if token)
    return {part.lower() for part in parts if part}


def _jaccard(left: Iterable[str], right: Iterable[str]) -> float:
    left_set = set(left)
    right_set = set(right)
    if not left_set or not right_set:
        return 0.0
    intersection = left_set & right_set
    union = left_set | right_set
    return len(intersection) / len(union)


def _score_graph_similarity(source_graph: Dict[str, Set[str]], candidate_graph: Dict[str, Set[str]]) -> tuple[float, List[str]]:
    score = 0.0
    reasons: List[str] = []

    feature_weights = {
        "callers": 0.18,
        "callees": 0.18,
        "bases": 0.08,
        "derived": 0.08,
        "type_users": 0.16,
        "used_types": 0.16,
        "field_users": 0.08,
        "used_fields": 0.08,
    }

    reason_labels = {
        "callers": "shared callers",
        "callees": "shared callees",
        "bases": "shared base types",
        "derived": "shared derived types",
        "type_users": "shared type users",
        "used_types": "shared used types",
        "field_users": "shared field users",
        "used_fields": "shared used fields",
    }

    for feature_name, weight in feature_weights.items():
        similarity = _jaccard(source_graph[feature_name], candidate_graph[feature_name])
        if similarity <= 0:
            continue
        score += similarity * weight
        reasons.append(f"{reason_labels[feature_name]} {similarity:.2f}")

    return score, reasons
