import os
from typing import List, Dict, Optional, Any
from sqlalchemy.orm import Session
from models import Project, Symbol
from graph import GraphLoader
from loguru import logger
from related import RelatedFinder


RULE_METADATA: Dict[str, Dict[str, Any]] = {
    "repograph.xaml": {"family": "repograph", "policy": "suppress", "priority": 5, "description": "synthetic XAML symbol"},
    "repograph.lambda": {"family": "repograph", "policy": "suppress", "priority": 5, "description": "synthetic lambda symbol"},
    "repograph.framework_method": {"family": "repograph", "policy": "suppress", "priority": 5, "description": "synthetic framework method"},
    "dotnet.host_entry_type": {"family": "dotnet", "policy": "suppress", "priority": 100, "description": "host entry type"},
    "dotnet.lifecycle_method": {"family": "dotnet", "policy": "suppress", "priority": 80, "description": "lifecycle method"},
    "dotnet.explicit_interface_implementation": {"family": "dotnet", "policy": "suppress", "priority": 85, "description": "explicit interface implementation"},
    "dotnet.extension_or_behavior_type": {"family": "dotnet", "policy": "suppress", "priority": 60, "description": "extension or behavior type"},
    "dotnet.host_builder_factory": {"family": "dotnet", "policy": "suppress", "priority": 85, "description": "host builder factory"},
    "xaml.codebehind_callback": {"family": "xaml", "policy": "label", "priority": 95, "description": "code-behind callback"},
    "xaml.partial_event_handler": {"family": "xaml", "policy": "label", "priority": 90, "description": "partial UI event handler"},
    "xaml.dependency_property_callback": {"family": "xaml", "policy": "label", "priority": 100, "description": "dependency property callback"},
    "ui.lifecycle_or_render_callback": {"family": "ui", "policy": "label", "priority": 70, "description": "UI lifecycle or render callback"},
    "ui.render_callback": {"family": "ui", "policy": "label", "priority": 75, "description": "render callback"},
    "ui.event_handler_signature": {"family": "ui", "policy": "label", "priority": 80, "description": "UI event handler signature"},
    "ui.event_handler_name": {"family": "ui", "policy": "label", "priority": 78, "description": "UI event handler naming pattern"},
    "ui.lifecycle_method": {"family": "ui", "policy": "label", "priority": 72, "description": "UI lifecycle method"},
    "ui.value_converter": {"family": "ui", "policy": "label", "priority": 88, "description": "UI value converter"},
    "mvvm.message_recipient": {"family": "mvvm", "policy": "label", "priority": 92, "description": "MVVM message recipient"},
    "mvvm.behavior_callback": {"family": "mvvm", "policy": "label", "priority": 82, "description": "MVVM behavior callback"},
    "mvvm.command_method": {"family": "mvvm", "policy": "label", "priority": 86, "description": "MVVM command method"},
    "aspnet.convention_type": {"family": "aspnet", "policy": "suppress", "priority": 70, "description": "ASP.NET convention type"},
    "aspnet.middleware_invoke": {"family": "aspnet", "policy": "suppress", "priority": 95, "description": "middleware invoke"},
    "aspnet.pipeline_extension": {"family": "aspnet", "policy": "suppress", "priority": 75, "description": "pipeline extension"},
    "aspnet.startup_method": {"family": "aspnet", "policy": "suppress", "priority": 100, "description": "startup method"},
    "aspnet.action_filter_callback": {"family": "aspnet", "policy": "suppress", "priority": 90, "description": "action filter callback"},
    "aspnet.controller_action": {"family": "aspnet", "policy": "suppress", "priority": 98, "description": "controller action"},
    "aspnet.endpoint_mapping": {"family": "aspnet", "policy": "suppress", "priority": 88, "description": "endpoint mapping"},
    "di.module_type": {"family": "di", "policy": "suppress", "priority": 70, "description": "DI module type"},
    "di.module_load": {"family": "di", "policy": "suppress", "priority": 90, "description": "DI module load"},
    "di.service_configuration": {"family": "di", "policy": "suppress", "priority": 92, "description": "DI service configuration"},
    "serialization.converter_type": {"family": "serialization", "policy": "suppress", "priority": 70, "description": "converter type"},
    "serialization.callback_method": {"family": "serialization", "policy": "label", "priority": 84, "description": "serialization callback"},
}


class IsolationAnalyzer:
    def __init__(self, session: Session, graph_loader: GraphLoader, reports_dir: str, include_ai_soft_edges: bool = False):
        self.session = session
        self.graph = graph_loader
        self.reports_dir = reports_dir
        self.include_ai_soft_edges = include_ai_soft_edges
        self.related_finder = RelatedFinder(session, graph_loader)
        os.makedirs(self.reports_dir, exist_ok=True)
        self._test_project_ids = {
            project_id
            for (project_id,) in self.session.query(Project.id).filter(Project.is_test_project == 1).all()
        }
        self._symbols_by_containing_type: Dict[str, List[Symbol]] = {}
        self._suppressed_by_rule: Dict[str, int] = {}
        self._suppressed_by_family: Dict[str, int] = {}
        self._labeled_by_rule: Dict[str, int] = {}
        self._labeled_by_family: Dict[str, int] = {}
        self._suppressed_by_ai_soft_edges: List[Dict[str, Any]] = []
        for symbol in self.session.query(Symbol).all():
            if symbol.containing_type:
                self._symbols_by_containing_type.setdefault(symbol.containing_type, []).append(symbol)

    def detect_candidates(self) -> List[Dict]:
        candidates = []
        self._suppressed_by_rule = {}
        self._suppressed_by_family = {}
        self._labeled_by_rule = {}
        self._labeled_by_family = {}
        self._suppressed_by_ai_soft_edges = []
        # fan_in == 0 のシンボルを取得（明示的なメソッド呼び出しがないもの）
        symbols = self.session.query(Symbol).filter(Symbol.fan_in == 0).all()
        
        for sym in symbols:
            fqn = sym.fqn
            fqn_lower = fqn.lower()
            file_path = sym.document.file_path if sym.document else ""
            file_name = sym.document.file_name if sym.document else ""

            convention_rules = self._get_convention_rules(sym, file_path)
            suppress_rules = [rule_id for rule_id in convention_rules if self._convention_rule_policy(rule_id) == "suppress"]
            if suppress_rules:
                for rule_id in suppress_rules:
                    self._record_suppression(rule_id)
                continue

            reason_labels = self._sort_rule_ids(
                [rule_id for rule_id in convention_rules if self._convention_rule_policy(rule_id) == "label"]
            )
            for rule_id in reason_labels:
                self._record_labeled_rule(rule_id)
            primary_reason_label = reason_labels[0] if reason_labels else None

            # 0. テスト資産は entrypoint / assertion / attribute / runner 規約が強く、静的参照だけで死活判定しにくい
            if self._is_test_artifact(sym, file_path):
                continue

            # 0. グラフ側で入辺が見えている場合は、DB の fan_in が古い/粗い可能性があるため除外
            if self._has_hard_graph_predecessors(fqn):
                continue
            ai_soft_predecessors = self._get_ai_soft_predecessors(fqn)
            if ai_soft_predecessors:
                self._suppressed_by_ai_soft_edges.append({
                    "fqn": sym.fqn,
                    "kind": sym.kind,
                    "file": file_name,
                    "sources": sorted({source for source, _edge_type in ai_soft_predecessors})[:8],
                    "edge_types": sorted({edge_type for _source, edge_type in ai_soft_predecessors})[:8],
                    "edge_count": len(ai_soft_predecessors),
                })
                continue
            
            # 1. 型依存関係グラフ（Type Dependency）によるセーフティネット
            if hasattr(self.graph, 'type_dependency_graph') and fqn in self.graph.type_dependency_graph:
                type_users = list(self.graph.type_dependency_graph.predecessors(fqn))
                if len(type_users) > 0:
                    continue # キャストやジェネリクス等で型として使われているため除外

            if sym.kind == "constructor" and sym.containing_type:
                if self._looks_like_ui_type_name(sym.containing_type):
                    continue

                if sym.containing_type in self.graph.type_dependency_graph:
                    type_users = list(self.graph.type_dependency_graph.predecessors(sym.containing_type))
                    if len(type_users) > 0:
                        continue
                    
            # 2. 継承グラフ（Inheritance）によるセーフティネット
            if sym.kind in ["class", "interface"]:
                if self._type_has_live_members(sym):
                    continue

                if fqn in self.graph.inheritance_graph:
                    derived_classes = list(self.graph.inheritance_graph.predecessors(fqn))
                    if len(derived_classes) > 0:
                        continue # 派生クラスが存在するため基底として機能しているとみなし除外

                # UIクラスは暗黙参照・デザイナ・リフレクション経由の利用が多く、構造グラフだけでは誤検出しやすい
                if self._looks_like_ui_type(sym):
                    continue
            
            # 3. ヒューリスティックな暗黙的呼び出しのノイズ除外
            if "main(" in fqn_lower: 
                continue
            if sym.kind == "method":
                if self._looks_like_accessor_method(sym):
                    continue

            candidates.append({
                "fqn": sym.fqn,
                "kind": sym.kind,
                "loc": sym.loc if sym.loc else 0,
                "file": file_name,
                "primary_reason_label": primary_reason_label,
                "reason_labels": reason_labels,
                "signals": self._collect_isolation_signals(sym),
                "explanation_facts": [],
                "why": "",
                "related": [],
                "category": "isolated",
            })
            
        # LOC（コード行数）が大きい＝削除時のリターンが大きい順にソート
        candidates.sort(key=lambda x: x["loc"], reverse=True)
        return candidates

    def generate_report(self):
        candidates = self.detect_candidates()
        self._attach_related_candidates(candidates)
        self._categorize_candidates(candidates)
        self._attach_explanations(candidates)
        candidates = self._prioritize_candidates(candidates)
        report_path = os.path.join(self.reports_dir, "dead_code_candidates.md")
        json_path = os.path.join(self.reports_dir, "dead_code_candidates.json")
        structural_report_path = os.path.join(self.reports_dir, "structural_isolation_candidates.md")
        structural_json_path = os.path.join(self.reports_dir, "structural_isolation_candidates.json")

        markdown = self._build_markdown_report(candidates)
        payload = self._build_json_report(candidates)

        for path in (report_path, structural_report_path):
            with open(path, "w", encoding="utf-8") as f:
                f.write(markdown)

        import json
        for path in (json_path, structural_json_path):
            with open(path, "w", encoding="utf-8") as f:
                json.dump(payload, f, indent=2, ensure_ascii=False)

        logger.info(f"Generated structural isolation report with {len(candidates)} candidates at {structural_report_path}")

    def detect_dead_code_candidates(self) -> List[Dict]:
        return self.detect_candidates()

    def _build_markdown_report(self, candidates: List[Dict]) -> str:
        lines: List[str] = []
        lines.append("# Structural Isolation Candidates\n\n")
        lines.append("> **Warning**: These are heuristic candidates based on structural graph analysis.\n")
        lines.append("> **Use them to investigate unresolved dependencies, framework callbacks, reflection, DI, or stale branches before any deletion decision.**\n\n")
        category_counts = self._summarize_categories(candidates)
        lines.append("## Investigation Categories\n\n")
        lines.append(f"- `isolated`: {category_counts['isolated']} candidates with weak or no nearby matches\n")
        lines.append(f"- `structural-sibling`: {category_counts['structural-sibling']} candidates with some structural similarity nearby\n")
        lines.append(f"- `near-family`: {category_counts['near-family']} candidates that strongly resemble existing family members or variants\n\n")
        if self._suppressed_by_rule:
            lines.append("## Suppressed Convention Patterns\n\n")
            lines.append("> These symbols were not listed as structural isolation candidates because they match known framework or language conventions.\n")
            lines.append("> Rule IDs are intentionally explicit so reviewers can see whether a suppression came from .NET hosting, XAML UI, MVVM, ASP.NET, DI, or serialization.\n\n")
            if self._suppressed_by_family:
                lines.append("| Family | Count |\n")
                lines.append("|--------|-------|\n")
                for family, count in sorted(self._suppressed_by_family.items()):
                    lines.append(f"| `{family}` | {count} |\n")
                lines.append("\n")
            lines.append("| Rule | Count |\n")
            lines.append("|------|-------|\n")
            for rule_id, count in sorted(self._suppressed_by_rule.items()):
                lines.append(f"| `{rule_id}` | {count} |\n")
            lines.append("\n")
        if self._labeled_by_rule:
            lines.append("## Convention Labels Kept In Candidates\n\n")
            lines.append("> These rules did not suppress candidates. They were kept as labels because they are still useful as investigation clues.\n\n")
            if self._labeled_by_family:
                lines.append("| Family | Count |\n")
                lines.append("|--------|-------|\n")
                for family, count in sorted(self._labeled_by_family.items()):
                    lines.append(f"| `{family}` | {count} |\n")
                lines.append("\n")
            lines.append("| Rule | Count |\n")
            lines.append("|------|-------|\n")
            for rule_id, count in sorted(self._labeled_by_rule.items()):
                lines.append(f"| `{rule_id}` | {count} |\n")
            lines.append("\n")
        if self._suppressed_by_ai_soft_edges:
            lines.append("## Suppressed By AI Soft Edges\n\n")
            lines.append("> These symbols still have `fan_in = 0` in the hard graph, but imported AI soft edges supplied additional inbound evidence.\n")
            lines.append("> Review these entries carefully before promoting the pattern into hard extraction rules.\n\n")
            lines.append("| Symbol | Kind | Soft Edge Count | Soft Edge Types | Soft Sources |\n")
            lines.append("|--------|------|-----------------|-----------------|--------------|\n")
            for item in self._suppressed_by_ai_soft_edges[:30]:
                edge_types = ", ".join(f"`{edge_type}`" for edge_type in item["edge_types"])
                sources = ", ".join(f"`{source}`" for source in item["sources"])
                lines.append(f"| `{item['fqn']}` | {item['kind']} | {item['edge_count']} | {edge_types} | {sources} |\n")
            lines.append("\n")
        lines.append("| Rank | Category | Primary Label | Labels | Related | LOC | Kind | Why It Looks Isolated | Symbol (FQN) | File |\n")
        lines.append("|------|----------|---------------|--------|---------|-----|------|------------------------|--------------|------|\n")
        for i, c in enumerate(candidates, 1):
            primary_label = f"`{c['primary_reason_label']}`" if c.get("primary_reason_label") else "-"
            label_text = ", ".join(f"`{label}`" for label in c.get("reason_labels", [])[:3]) or "-"
            lines.append(f"| {i} | `{c['category']}` | {primary_label} | {label_text} | {len(c['related'])} | {c['loc']} | {c['kind']} | {c['why']} | `{c['fqn']}` | {c['file']} |\n")

        detailed_candidates = candidates[:20]
        if detailed_candidates:
            lines.append("\n## Why These Candidates Surfaced\n\n")
            lines.append("> [!NOTE]\n")
            lines.append("> The notes below summarize which structural signals were missing and whether RepoGraph found nearby family members.\n\n")
            for candidate in detailed_candidates:
                lines.append(f"<details><summary>`{candidate['fqn']}`</summary>\n\n")
                lines.append(f"- category: `{candidate['category']}`\n")
                if candidate.get("primary_reason_label"):
                    lines.append(f"- primary reason label: `{candidate['primary_reason_label']}`\n")
                if candidate.get("reason_labels"):
                    lines.append(f"- reason labels: {', '.join(f'`{label}`' for label in candidate['reason_labels'])}\n")
                lines.append(f"- why: {candidate['why']}\n")
                signal_summary = self._format_signal_summary(candidate["signals"])
                if signal_summary:
                    lines.append(f"- structural signals: {signal_summary}\n")
                explanation_facts = candidate.get("explanation_facts", [])
                if explanation_facts:
                    lines.append("- explanation facts:\n")
                    for fact in explanation_facts[:6]:
                        lines.append(f"  - {fact}\n")
                if candidate["related"]:
                    lines.append("- nearby symbols:\n")
                    for related in candidate["related"]:
                        reason_text = ", ".join(related["reasons"][:4])
                        line = f"  - `{related['fqn']}` ({related['kind']}, score {related['score']:.4f})"
                        if reason_text:
                            line += f" -- {reason_text}"
                        lines.append(line + "\n")
                else:
                    lines.append("- nearby symbols: none above the similarity threshold\n")
                lines.append("\n</details>\n\n")

        annotated_candidates = [candidate for candidate in candidates[:20] if candidate["related"]]
        if annotated_candidates:
            lines.append("## Related Existing Implementations\n\n")
            lines.append("> [!TIP]\n")
            lines.append("> These candidates have nearby existing symbols with similar naming or structural context.\n")
            lines.append("> Use this section to check whether the candidate is actually a duplicate family member, a callback slot, or an unresolved dependency surface.\n\n")
            for candidate in annotated_candidates:
                lines.append(f"<details><summary>`{candidate['fqn']}`</summary>\n\n")
                for related in candidate["related"]:
                    lines.append(f"- `{related['fqn']}` ({related['kind']}, score {related['score']:.4f})\n")
                    reason_text = ", ".join(related["reasons"][:4])
                    if reason_text:
                        lines.append(f"  reasons: {reason_text}\n")
                lines.append("\n</details>\n\n")
        return "".join(lines)

    def _build_json_report(self, candidates: List[Dict]) -> Dict[str, Any]:
        return {
            "analysis_mode": "hard+ai-soft-edges" if self.include_ai_soft_edges else "hard-only",
            "report_kind": "structural_isolation_candidates",
            "candidates": candidates,
            "rule_family_summary": self._summarize_rule_families(candidates),
            "rule_mode_summary": self._summarize_rule_modes(candidates),
            "suppressed_by_rule": self._suppressed_by_rule,
            "suppressed_by_family": self._suppressed_by_family,
            "labeled_by_rule": self._labeled_by_rule,
            "labeled_by_family": self._labeled_by_family,
            "suppressed_by_ai_soft_edges": self._suppressed_by_ai_soft_edges,
        }

    @staticmethod
    def _summarize_rule_families(candidates: List[Dict]) -> Dict[str, Dict[str, int]]:
        summary: Dict[str, Dict[str, int]] = {
            "call_inbound": {},
            "call_outbound": {},
            "type_inbound": {},
            "type_outbound": {},
        }

        signal_keys = {
            "call_inbound": "inbound_call_rule_families",
            "call_outbound": "outbound_call_rule_families",
            "type_inbound": "inbound_type_rule_families",
            "type_outbound": "outbound_type_rule_families",
        }

        for candidate in candidates:
            signals = candidate.get("signals", {})
            for bucket, signal_key in signal_keys.items():
                for family, count in signals.get(signal_key, {}).items():
                    summary[bucket][family] = summary[bucket].get(family, 0) + int(count)

        return summary

    @staticmethod
    def _summarize_rule_modes(candidates: List[Dict]) -> Dict[str, Dict[str, int]]:
        summary: Dict[str, Dict[str, int]] = {
            "call_inbound": {},
            "call_outbound": {},
            "type_inbound": {},
            "type_outbound": {},
        }

        signal_keys = {
            "call_inbound": "inbound_call_rule_modes",
            "call_outbound": "outbound_call_rule_modes",
            "type_inbound": "inbound_type_rule_modes",
            "type_outbound": "outbound_type_rule_modes",
        }

        for candidate in candidates:
            signals = candidate.get("signals", {})
            for bucket, signal_key in signal_keys.items():
                for mode, count in signals.get(signal_key, {}).items():
                    summary[bucket][mode] = summary[bucket].get(mode, 0) + int(count)

        return summary

    def _attach_related_candidates(self, candidates: List[Dict]) -> None:
        for candidate in candidates[:50]:
            symbol = self.related_finder.find_symbol(candidate["fqn"])
            if symbol is None:
                continue
            related = self.related_finder.find_related(symbol, top_k=3)
            candidate["related"] = [item.to_dict() for item in related if item.score >= 0.45]

    def _attach_explanations(self, candidates: List[Dict]) -> None:
        for candidate in candidates:
            explanation = self._build_candidate_explanation(candidate)
            candidate["why"] = explanation["summary"]
            candidate["explanation_facts"] = explanation["facts"]

    @staticmethod
    def _categorize_candidates(candidates: List[Dict]) -> None:
        for candidate in candidates:
            candidate["category"] = IsolationAnalyzer._classify_candidate(candidate)

    @staticmethod
    def _classify_candidate(candidate: Dict) -> str:
        if not candidate["related"]:
            return "isolated"

        strongest = candidate["related"][0]
        reasons = strongest.get("reasons", [])
        score = strongest.get("score", 0.0)
        has_locality = any(reason in {"same containing type", "same namespace"} for reason in reasons)
        has_name_family = any(reason.startswith("name tokens ") for reason in reasons)
        has_structural_overlap = any(
            reason.startswith(prefix)
            for reason in reasons
            for prefix in ("shared callers", "shared callees", "shared used types", "shared used fields")
        )

        if score >= 0.7 and has_locality and has_name_family:
            return "near-family"

        if score >= 0.55 and (has_structural_overlap or has_locality):
            return "structural-sibling"

        return "isolated"

    @staticmethod
    def _prioritize_candidates(candidates: List[Dict]) -> List[Dict]:
        category_order = {
            "isolated": 0,
            "structural-sibling": 1,
            "near-family": 2,
        }
        return sorted(
            candidates,
            key=lambda candidate: (
                category_order.get(candidate["category"], 99),
                -candidate["loc"],
                candidate["fqn"],
            ),
        )

    @staticmethod
    def _summarize_categories(candidates: List[Dict]) -> Dict[str, int]:
        counts = {
            "isolated": 0,
            "structural-sibling": 0,
            "near-family": 0,
        }
        for candidate in candidates:
            counts[candidate["category"]] = counts.get(candidate["category"], 0) + 1
        return counts

    def _collect_isolation_signals(self, sym: Symbol) -> Dict[str, int]:
        fqn = sym.fqn
        signals = {
            "callers": self._graph_predecessor_count("call_graph", fqn),
            "type_users": self._graph_predecessor_count("type_dependency_graph", fqn),
            "field_users": self._graph_predecessor_count("field_access_graph", fqn),
            "derived_types": self._graph_predecessor_count("inheritance_graph", fqn),
            "callees": self._graph_successor_count("call_graph", fqn),
            "used_types": self._graph_successor_count("type_dependency_graph", fqn),
            "used_fields": self._graph_successor_count("field_access_graph", fqn),
            "inbound_call_types": self.graph.inbound_edge_type_counts("call_graph", fqn),
            "outbound_call_types": self.graph.outbound_edge_type_counts("call_graph", fqn),
            "inbound_call_rule_families": self.graph.inbound_edge_rule_family_counts("call_graph", fqn),
            "outbound_call_rule_families": self.graph.outbound_edge_rule_family_counts("call_graph", fqn),
            "inbound_call_rule_ids": self.graph.inbound_edge_rule_id_counts("call_graph", fqn),
            "outbound_call_rule_ids": self.graph.outbound_edge_rule_id_counts("call_graph", fqn),
            "inbound_call_rule_modes": self.graph.inbound_edge_rule_mode_counts("call_graph", fqn),
            "outbound_call_rule_modes": self.graph.outbound_edge_rule_mode_counts("call_graph", fqn),
            "inbound_type_rule_families": self.graph.inbound_edge_rule_family_counts("type_dependency_graph", fqn),
            "outbound_type_rule_families": self.graph.outbound_edge_rule_family_counts("type_dependency_graph", fqn),
            "inbound_type_rule_ids": self.graph.inbound_edge_rule_id_counts("type_dependency_graph", fqn),
            "outbound_type_rule_ids": self.graph.outbound_edge_rule_id_counts("type_dependency_graph", fqn),
            "inbound_type_rule_modes": self.graph.inbound_edge_rule_mode_counts("type_dependency_graph", fqn),
            "outbound_type_rule_modes": self.graph.outbound_edge_rule_mode_counts("type_dependency_graph", fqn),
        }

        if sym.containing_type:
            signals["containing_type_users"] = self._graph_predecessor_count("type_dependency_graph", sym.containing_type)

        return signals

    def _build_candidate_explanation(self, candidate: Dict[str, Any]) -> Dict[str, Any]:
        signals = candidate["signals"]
        missing = []
        facts = []
        if signals.get("callers", 0) == 0:
            missing.append("no callers")
            facts.append("call graph has no inbound callers")
        if signals.get("type_users", 0) == 0:
            missing.append("no type users")
            facts.append("type dependency graph has no inbound type users")
        if signals.get("field_users", 0) == 0 and candidate["kind"] in {"field", "property"}:
            missing.append("no field readers/writers")
            facts.append("field access graph shows no inbound readers or writers")
        if candidate["kind"] in {"class", "interface"} and signals.get("derived_types", 0) == 0:
            missing.append("no derived types")
            facts.append("inheritance graph has no derived types")
        if candidate["kind"] == "constructor" and signals.get("containing_type_users", 0) == 0:
            missing.append("owning type has no detected type users")
            facts.append("constructor's owning type has no inbound type users")
        if signals.get("callees", 0) > 0:
            facts.append(f"symbol still calls {signals['callees']} downstream method(s)")
        if signals.get("used_types", 0) > 0:
            facts.append(f"symbol still depends on {signals['used_types']} downstream type(s)")
        inbound_call_types = signals.get("inbound_call_types", {})
        if inbound_call_types:
            facts.append(f"inbound call edge types: {self._format_edge_type_counts(inbound_call_types)}")
        outbound_call_types = signals.get("outbound_call_types", {})
        if outbound_call_types:
            facts.append(f"outbound call edge types: {self._format_edge_type_counts(outbound_call_types)}")
        inbound_call_rule_families = signals.get("inbound_call_rule_families", {})
        if inbound_call_rule_families:
            facts.append(f"inbound call rule families: {self._format_edge_type_counts(inbound_call_rule_families)}")
        outbound_call_rule_families = signals.get("outbound_call_rule_families", {})
        if outbound_call_rule_families:
            facts.append(f"outbound call rule families: {self._format_edge_type_counts(outbound_call_rule_families)}")
        outbound_call_rule_ids = signals.get("outbound_call_rule_ids", {})
        if outbound_call_rule_ids:
            facts.append(f"outbound call rule ids: {self._format_edge_type_counts(outbound_call_rule_ids)}")
        outbound_call_rule_modes = signals.get("outbound_call_rule_modes", {})
        if outbound_call_rule_modes:
            facts.append(f"outbound call rule modes: {self._format_edge_type_counts(outbound_call_rule_modes)}")
        inbound_type_rule_families = signals.get("inbound_type_rule_families", {})
        if inbound_type_rule_families:
            facts.append(f"inbound type rule families: {self._format_edge_type_counts(inbound_type_rule_families)}")
        outbound_type_rule_families = signals.get("outbound_type_rule_families", {})
        if outbound_type_rule_families:
            facts.append(f"outbound type rule families: {self._format_edge_type_counts(outbound_type_rule_families)}")
        outbound_type_rule_ids = signals.get("outbound_type_rule_ids", {})
        if outbound_type_rule_ids:
            facts.append(f"outbound type rule ids: {self._format_edge_type_counts(outbound_type_rule_ids)}")
        outbound_type_rule_modes = signals.get("outbound_type_rule_modes", {})
        if outbound_type_rule_modes:
            facts.append(f"outbound type rule modes: {self._format_edge_type_counts(outbound_type_rule_modes)}")

        framework_context = self._build_framework_context_summary(signals)
        if framework_context:
            facts.append(framework_context)

        if not missing:
            missing.append("no inbound structural references")
            facts.append("all checked structural graphs lacked inbound references")

        if candidate["related"]:
            strongest = candidate["related"][0]
            reason_text = ", ".join(strongest.get("reasons", [])[:2])
            if reason_text:
                facts.append(f"nearest related symbol suggests family resemblance: {reason_text}")
                summary = f"{'; '.join(missing)}; nearest sibling looks like {reason_text}"
                if framework_context:
                    summary += f"; framework context: {framework_context.removeprefix('framework context: ')}"
                return {"summary": summary, "facts": facts}
            facts.append("related-symbol search found a nearby structural sibling")
            summary = f"{'; '.join(missing)}; has nearby structural sibling"
            if framework_context:
                summary += f"; framework context: {framework_context.removeprefix('framework context: ')}"
            return {"summary": summary, "facts": facts}

        summary = "; ".join(missing)
        if framework_context:
            summary += f"; framework context: {framework_context.removeprefix('framework context: ')}"
        return {"summary": summary, "facts": facts}

    @staticmethod
    def _build_framework_context_summary(signals: Dict[str, Any]) -> str:
        contexts = []
        for signal_key, label in (
            ("inbound_call_rule_families", "inbound calls"),
            ("outbound_call_rule_families", "outbound calls"),
            ("inbound_type_rule_families", "inbound type usage"),
            ("outbound_type_rule_families", "outbound type usage"),
        ):
            families = signals.get(signal_key, {})
            if not families:
                continue
            family_names = ", ".join(sorted(families))
            contexts.append(f"{label} touch {family_names}")

        if not contexts:
            return ""

        return f"framework context: {'; '.join(contexts)}"

    @staticmethod
    def _format_signal_summary(signals: Dict[str, int]) -> str:
        labels = (
            ("callers", "callers"),
            ("type_users", "type users"),
            ("field_users", "field users"),
            ("derived_types", "derived types"),
            ("callees", "callees"),
            ("used_types", "used types"),
            ("used_fields", "used fields"),
            ("containing_type_users", "owning type users"),
        )
        return ", ".join(f"{label}={signals.get(key, 0)}" for key, label in labels if key in signals)

    def _record_suppression(self, rule_id: str) -> None:
        self._suppressed_by_rule[rule_id] = self._suppressed_by_rule.get(rule_id, 0) + 1
        family = self._rule_family(rule_id)
        self._suppressed_by_family[family] = self._suppressed_by_family.get(family, 0) + 1

    def _record_labeled_rule(self, rule_id: str) -> None:
        self._labeled_by_rule[rule_id] = self._labeled_by_rule.get(rule_id, 0) + 1
        family = self._rule_family(rule_id)
        self._labeled_by_family[family] = self._labeled_by_family.get(family, 0) + 1

    @staticmethod
    def _rule_family(rule_id: str) -> str:
        metadata = RULE_METADATA.get(rule_id, {})
        if "family" in metadata:
            return metadata["family"]
        if "." not in rule_id:
            return rule_id
        return rule_id.split(".", 1)[0]

    @staticmethod
    def _convention_rule_policy(rule_id: str) -> str:
        return str(RULE_METADATA.get(rule_id, {}).get("policy", "suppress"))

    @staticmethod
    def _rule_priority(rule_id: str) -> int:
        return int(RULE_METADATA.get(rule_id, {}).get("priority", 0))

    @classmethod
    def _sort_rule_ids(cls, rule_ids: List[str]) -> List[str]:
        return sorted(set(rule_ids), key=lambda rule_id: (-cls._rule_priority(rule_id), rule_id))

    def _get_convention_rules(self, sym: Symbol, file_path: str) -> List[str]:
        rules: List[str] = []
        if sym.kind in {"xaml", "lambda", "framework_method"}:
            rules.append(f"repograph.{sym.kind}")
            return self._sort_rule_ids(rules)

        if sym.kind in {"class", "interface"}:
            rule_id = self._get_type_convention_rule(sym, file_path)
            return self._sort_rule_ids([rule_id] if rule_id else [])

        if sym.kind != "method":
            return []

        for detector in (
            self._get_dotnet_lifecycle_rule,
            self._get_ui_framework_rules,
            self._get_mvvm_rule,
            self._get_aspnet_hosting_rule,
            self._get_di_container_rule,
            self._get_serialization_rule,
        ):
            detected = detector(sym, file_path)
            if isinstance(detected, list):
                rules.extend([rule_id for rule_id in detected if rule_id])
            elif detected:
                rules.append(detected)

        if self._looks_like_explicit_interface_method(sym):
            rules.append("dotnet.explicit_interface_implementation")

        return self._sort_rule_ids(rules)

    @staticmethod
    def _get_type_convention_rule(sym: Symbol, file_path: str = "") -> Optional[str]:
        name = (sym.name or "").split(".")[-1]
        path_lower = file_path.lower()
        if name in {"App", "Program", "Startup"}:
            return "dotnet.host_entry_type"
        if name.endswith(("Controller", "Middleware", "ActionFilter")) and any(
            marker in path_lower for marker in ("\\controllers\\", "\\middleware\\", "\\filters\\")
        ):
            return "aspnet.convention_type"
        if name.endswith(("Module",)):
            return "di.module_type"
        if name.endswith(("Converter",)):
            return "serialization.converter_type"
        if name.endswith(("Behavior", "Extension", "Extensions", "ExtensionMethods")):
            return "dotnet.extension_or_behavior_type"
        return None

    @staticmethod
    def _get_dotnet_lifecycle_rule(sym: Symbol, file_path: str) -> Optional[str]:
        del file_path
        name = (sym.name or "").lower()
        fqn_lower = (sym.fqn or "").lower()

        if name in {"main", "mainasync", "dispose"}:
            return "dotnet.lifecycle_method"

        if any(token in fqn_lower for token in (
            ".dispose(", ".wndproc(", ".initializecomponent("
        )):
            return "dotnet.lifecycle_method"

        return None

    def _get_ui_framework_rules(self, sym: Symbol, file_path: str) -> List[str]:
        name = sym.name or ""
        name_lower = name.lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()
        fqn_lower = (sym.fqn or "").lower()
        file_path_lower = file_path.lower()
        rules: List[str] = []

        if name_lower in {
            "onstartup", "oninitialize", "onactivated", "onapplytemplate",
            "onframeworkinitializationcompleted",
            "render", "drawitem", "ondrawitem", "onrender", "onupdate",
            "onpointerpressed", "onpointermoved", "onpointerreleased",
            "onkeydown", "onkeyup", "ontextinput", "onloaded",
            "onselectionchanged", "onviewmodelpropertychanged",
        }:
            rules.append("ui.lifecycle_or_render_callback")

        if name_lower in {"render", "drawitem", "onupdate"} and containing_type.endswith((
            "renderer", "shape", "annotation", "control", "tool"
        )):
            rules.append("ui.render_callback")

        if (file_path_lower.endswith(".axaml.cs") or file_path_lower.endswith(".xaml.cs")) and name.startswith("On"):
            rules.append("xaml.codebehind_callback")

        if self._looks_like_partial_ui_event_handler(sym):
            rules.append("xaml.partial_event_handler")

        if name_lower.endswith("propertychanged") and "(dependencyobject, dependencypropertychangedeventargs)" in fqn_lower:
            rules.append("xaml.dependency_property_callback")

        if sym.containing_type and self._looks_like_ui_type_name(sym.containing_type):
            if self._looks_like_ui_event_signature(fqn_lower):
                rules.append("ui.event_handler_signature")

            if any(evt in fqn_lower for evt in (
                "_click(", "_load(", "_changed(", "_tick(", "_shown(",
                "_checkedchanged(", "_selectedindexchanged(", "_textchanged(",
                "_valuechanged(", "_formclosed(", "_formclosing(", "_resize(",
                "_paint(", "_dragdrop(", "_dragenter(", "_keydown(",
                "_keyup(", "_keypress(", "_dowork(", "_runworkercompleted(",
                "_progresschanged(", "_mouseup(", "_mousedown(",
                "_mousemove(", "_doubleclick(", "_afterselect(",
                "_cellcontentclick(", "_elapsed("
            )):
                rules.append("ui.event_handler_name")

        if any(token in fqn_lower for token in (
            ".onpaint(", ".onload(", ".onshown(", ".onclosing(", ".onclosed("
        )):
            rules.append("ui.lifecycle_method")

        return self._sort_rule_ids(rules)

    def _get_mvvm_rule(self, sym: Symbol, file_path: str) -> Optional[str]:
        del file_path
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()

        if self._looks_like_message_recipient_method(sym):
            return "mvvm.message_recipient"

        if name.startswith("handle") and containing_type.endswith(("behavior", "adapter")):
            return "mvvm.behavior_callback"

        if self._looks_like_command_method(sym):
            return "mvvm.command_method"

        return None

    @staticmethod
    def _get_aspnet_hosting_rule(sym: Symbol, file_path: str) -> Optional[str]:
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()
        path_lower = file_path.lower()

        if name in {"invoke", "invokeasync"} and (
            containing_type.endswith("middleware")
            or "\\middleware\\" in path_lower
        ):
            return "aspnet.middleware_invoke"

        if name.startswith("use") and containing_type.endswith(("extensions", "middleware")):
            return "aspnet.pipeline_extension"

        if name in {"configure", "configureservices"} and containing_type == "startup":
            return "aspnet.startup_method"

        if name in {"onactionexecuting", "onactionexecuted"} and (
            containing_type.endswith("filter")
            or "\\controllers\\" in path_lower
            or "\\filters\\" in path_lower
        ):
            return "aspnet.action_filter_callback"

        if "\\controllers\\" in path_lower and containing_type.endswith("controller"):
            if (sym.accessibility or "").lower() == "public":
                return "aspnet.controller_action"

        if name.startswith("map") and (
            containing_type.endswith(("endpoint", "endpoints", "extensions"))
            or "\\endpoints\\" in path_lower
        ):
            return "aspnet.endpoint_mapping"

        if name.startswith("create") and containing_type.endswith(("webserver", "host", "builder")):
            return "dotnet.host_builder_factory"

        return None

    @staticmethod
    def _get_di_container_rule(sym: Symbol, file_path: str) -> Optional[str]:
        del file_path
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()

        if name == "load" and containing_type.endswith("module"):
            return "di.module_load"

        if name in {"configure", "configureservices"}:
            return "di.service_configuration"

        return None

    @staticmethod
    def _get_serialization_rule(sym: Symbol, file_path: str) -> Optional[str]:
        del file_path
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()

        if name in {"convert", "convertback"}:
            return "ui.value_converter"

        if containing_type.endswith(("converter", "contractresolver")) and name in {
            "readjson", "writejson", "canconvert", "createproperty",
            "createdictionarycontract", "createcontract"
        }:
            return "serialization.callback_method"

        return None

    @staticmethod
    def _looks_like_ui_type(sym: Symbol) -> bool:
        name = sym.name or ""
        file_name = sym.document.file_name if sym.document else ""

        if IsolationAnalyzer._looks_like_ui_type_name(name):
            return True

        if ".Designer." in file_name:
            return True

        return False

    @staticmethod
    def _looks_like_framework_convention_type(sym: Symbol) -> bool:
        return IsolationAnalyzer._get_type_convention_rule(sym) is not None

    @staticmethod
    def _looks_like_ui_type_name(name: str) -> bool:
        ui_suffixes = (
            "Form", "Control", "Dialog", "Window", "Page",
            "View", "Panel"
        )
        short_name = name.split(".")[-1] if name else ""
        return bool(short_name) and (
            short_name.endswith(ui_suffixes)
            or short_name.startswith("Form")
        )

    @staticmethod
    def _looks_like_framework_convention_method(sym: Symbol) -> bool:
        file_path = sym.document.file_path if sym.document and sym.document.file_path else ""
        return IsolationAnalyzer._get_serialization_rule(sym, file_path) is not None

    def _looks_like_message_recipient_method(self, sym: Symbol) -> bool:
        name = (sym.name or "").lower()
        fqn_lower = (sym.fqn or "").lower()
        if name != "receive":
            return False

        if ".messages." in fqn_lower or "message)" in fqn_lower or "message," in fqn_lower:
            return True

        if sym.containing_type and self._containing_type_implements(sym.containing_type, "IRecipient"):
            return True

        if not sym.containing_type:
            return False

        siblings = self._symbols_by_containing_type.get(sym.containing_type, [])
        receive_methods = [member for member in siblings if member.kind == "method" and (member.name or "").lower() == "receive"]
        return len(receive_methods) >= 2 and any(
            ".messages." in (member.fqn or "").lower()
            or "message)" in (member.fqn or "").lower()
            or "message," in (member.fqn or "").lower()
            for member in receive_methods
        )

    @staticmethod
    def _looks_like_explicit_interface_method(sym: Symbol) -> bool:
        if not sym.containing_type:
            return False

        fqn = sym.fqn or ""
        if not fqn.startswith(sym.containing_type + "."):
            return False

        suffix = fqn[len(sym.containing_type) + 1:]
        method_part = suffix.split("(", 1)[0]
        return "." in method_part

    def _is_test_artifact(self, sym: Symbol, file_path: str) -> bool:
        if sym.project_id in self._test_project_ids:
            return True

        combined = f"{sym.fqn} {file_path}".lower()
        markers = (
            ".unittest.", ".tests.", ".test.", "unittest\\", "tests\\", "test\\",
            "\\integrationtests\\", "\\livetests\\", "\\browser.tests\\"
        )
        return any(marker in combined for marker in markers)

    @staticmethod
    def _looks_like_host_integration_method(sym: Symbol, file_path: str) -> bool:
        return IsolationAnalyzer._get_aspnet_hosting_rule(sym, file_path) is not None

    @staticmethod
    def _looks_like_ui_event_signature(fqn_lower: str) -> bool:
        if "(object," not in fqn_lower:
            return False

        return (
            "eventargs)" in fqn_lower
            or "eventargs," in fqn_lower
            or "(object, system.windows.forms." in fqn_lower
            or "(object, system.componentmodel." in fqn_lower
        )

    def _looks_like_command_method(self, sym: Symbol) -> bool:
        name = (sym.name or "").lower()
        if not name or not sym.containing_type:
            return False

        if not (sym.containing_type.split(".")[-1].lower().endswith("viewmodel")):
            return False

        siblings = self._symbols_by_containing_type.get(sym.containing_type, [])
        command_names = {
            (member.name or "").lower()
            for member in siblings
            if member.kind in {"field", "property"}
        }
        return f"{name}command" in command_names or f"{name}_command" in command_names

    def _containing_type_implements(self, containing_type: str, interface_name_fragment: str) -> bool:
        graph = getattr(self.graph, "inheritance_graph", None)
        if graph is None or containing_type not in graph:
            return False

        fragment = interface_name_fragment.lower()
        try:
            return any(fragment in base_fqn.lower() for base_fqn in graph.successors(containing_type))
        except Exception:
            return False

    def _type_has_live_members(self, sym: Symbol) -> bool:
        members = self._symbols_by_containing_type.get(sym.fqn, [])
        for member in members:
            if member.fan_in and member.fan_in > 0:
                return True

            if self._has_hard_graph_predecessors(member.fqn):
                return True

        return False

    @staticmethod
    def _looks_like_partial_ui_event_handler(sym: Symbol) -> bool:
        name = (sym.name or "")
        file_path = sym.document.file_path.lower() if sym.document and sym.document.file_path else ""
        containing_type = sym.containing_type or ""

        if not containing_type:
            return False

        if not any(part in file_path for part in (".axaml.cs", ".xaml.cs", "\\views\\", "\\controls\\")):
            return False

        if name.startswith("On") and (
            name.endswith("Requested")
            or name.endswith("Changed")
            or name.endswith("Activated")
            or name.endswith("Selected")
        ):
            return True

        return False

    @staticmethod
    def _looks_like_accessor_method(sym: Symbol) -> bool:
        fqn = sym.fqn or ""
        name = sym.name or ""
        return (
            fqn.endswith(".get")
            or fqn.endswith(".set")
            or name in {"get", "set", "add", "remove"}
        )

    def _has_hard_graph_predecessors(self, fqn: str) -> bool:
        graph = getattr(self.graph, "call_graph", None)
        if graph is not None and fqn in graph:
            try:
                if any(True for _ in graph.predecessors(fqn)):
                    return True
            except Exception:
                pass

        return False

    def _get_ai_soft_predecessors(self, fqn: str) -> List[tuple[str, str]]:
        if not self.include_ai_soft_edges:
            return []

        soft_graph = getattr(self.graph, "ai_soft_graph", None)
        if soft_graph is None or fqn not in soft_graph:
            return []

        try:
            return [
                (source, data.get("type", "ai_soft_edge"))
                for source, _target, data in soft_graph.in_edges(fqn, data=True)
            ]
        except Exception:
            return []

    def _graph_predecessor_count(self, graph_name: str, fqn: str) -> int:
        graph = getattr(self.graph, graph_name, None)
        if graph is None or fqn not in graph:
            return 0

        try:
            return sum(1 for _ in graph.predecessors(fqn))
        except Exception:
            return 0

    def _graph_successor_count(self, graph_name: str, fqn: str) -> int:
        graph = getattr(self.graph, graph_name, None)
        if graph is None or fqn not in graph:
            return 0

        try:
            return sum(1 for _ in graph.successors(fqn))
        except Exception:
            return 0

    @staticmethod
    def _format_edge_type_counts(counts: Dict[str, int]) -> str:
        parts = [f"{edge_type}={count}" for edge_type, count in sorted(counts.items())]
        return ", ".join(parts)


DeadCodeDetector = IsolationAnalyzer
