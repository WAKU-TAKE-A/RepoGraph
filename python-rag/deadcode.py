import os
from typing import List, Dict
from sqlalchemy.orm import Session
from models import Project, Symbol
from graph import GraphLoader
from loguru import logger
from related import RelatedFinder

class DeadCodeDetector:
    def __init__(self, session: Session, graph_loader: GraphLoader, reports_dir: str):
        self.session = session
        self.graph = graph_loader
        self.reports_dir = reports_dir
        self.related_finder = RelatedFinder(session, graph_loader)
        os.makedirs(self.reports_dir, exist_ok=True)
        self._test_project_ids = {
            project_id
            for (project_id,) in self.session.query(Project.id).filter(Project.is_test_project == 1).all()
        }
        self._symbols_by_containing_type: Dict[str, List[Symbol]] = {}
        for symbol in self.session.query(Symbol).all():
            if symbol.containing_type:
                self._symbols_by_containing_type.setdefault(symbol.containing_type, []).append(symbol)

    def detect_dead_code_candidates(self) -> List[Dict]:
        candidates = []
        # fan_in == 0 のシンボルを取得（明示的なメソッド呼び出しがないもの）
        symbols = self.session.query(Symbol).filter(Symbol.fan_in == 0).all()
        
        for sym in symbols:
            fqn = sym.fqn
            fqn_lower = fqn.lower()
            file_path = sym.document.file_path if sym.document else ""
            file_name = sym.document.file_name if sym.document else ""

            if sym.kind in {"xaml", "lambda", "framework_method"}:
                continue

            # 0. テスト資産は entrypoint / assertion / attribute / runner 規約が強く、静的参照だけで死活判定しにくい
            if self._is_test_artifact(sym, file_path):
                continue

            # 0. グラフ側で入辺が見えている場合は、DB の fan_in が古い/粗い可能性があるため除外
            if self._has_graph_predecessors(fqn):
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
                if self._looks_like_framework_convention_type(sym):
                    continue

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

                if self._looks_like_message_recipient_method(sym):
                    continue

                if self._looks_like_explicit_interface_method(sym):
                    continue

                if self._looks_like_framework_convention_method(sym):
                    continue

                if self._looks_like_host_integration_method(sym, file_path):
                    continue

                if self._looks_like_partial_ui_event_handler(sym):
                    continue

                if sym.containing_type and self._looks_like_ui_type_name(sym.containing_type):
                    if any(sig in fqn_lower for sig in [
                        "(object, system.eventargs)",
                        "(object, system.windows.forms.",
                        "(object, halcondotnet.",
                        "(object, system.componentmodel."
                    ]):
                        continue

                # 一般的なUIイベントハンドラのサフィックス
                if any(evt in fqn_lower for evt in [
                    "_click(", "_load(", "_changed(", "_tick(", "_shown(",
                    "_checkedchanged(", "_selectedindexchanged(", "_textchanged(",
                    "_valuechanged(", "_formclosed(", "_formclosing(", "_resize(",
                    "_paint(", "_dragdrop(", "_dragenter(", "_keydown(",
                    "_keyup(", "_keypress(", "_dowork(", "_runworkercompleted(",
                    "_progresschanged(", "_mouseup(", "_mousedown(",
                    "_mousemove(", "_doubleclick(", "_afterselect(",
                    "_cellcontentclick(", "_elapsed("
                ]):
                    continue
                # 一般的なフレームワークライフサイクル
                if any(sys in fqn_lower for sys in [
                    ".dispose(", ".wndproc(", ".onpaint(", ".onload(",
                    ".onshown(", ".onclosing(", ".onclosed(", ".initializecomponent("
                ]):
                    continue
            candidates.append({
                "fqn": sym.fqn,
                "kind": sym.kind,
                "loc": sym.loc if sym.loc else 0,
                "file": file_name,
                "signals": self._collect_isolation_signals(sym),
                "why": "",
                "related": [],
                "category": "isolated",
            })
            
        # LOC（コード行数）が大きい＝削除時のリターンが大きい順にソート
        candidates.sort(key=lambda x: x["loc"], reverse=True)
        return candidates

    def generate_report(self):
        candidates = self.detect_dead_code_candidates()
        self._attach_related_candidates(candidates)
        self._categorize_candidates(candidates)
        self._attach_explanations(candidates)
        candidates = self._prioritize_candidates(candidates)
        report_path = os.path.join(self.reports_dir, "dead_code_candidates.md")
        json_path = os.path.join(self.reports_dir, "dead_code_candidates.json")
        
        with open(report_path, "w", encoding="utf-8") as f:
            f.write("# Dead Code Candidates\n\n")
            f.write("> **⚠️ Warning**: These are heuristic candidates based on structural graph analysis (Fan-in = 0, No Derived Classes, No Type Usages).\n")
            f.write("> **Always verify with AI text search (grep) or IDE references before deletion.**\n\n")
            category_counts = self._summarize_categories(candidates)
            f.write("## Investigation Categories\n\n")
            f.write(f"- `isolated`: {category_counts['isolated']} candidates with weak or no nearby matches\n")
            f.write(f"- `structural-sibling`: {category_counts['structural-sibling']} candidates with some structural similarity nearby\n")
            f.write(f"- `near-family`: {category_counts['near-family']} candidates that strongly resemble existing family members or variants\n\n")
            f.write("| Rank | Category | Related | LOC | Kind | Why It Looks Isolated | Symbol (FQN) | File |\n")
            f.write("|------|----------|---------|-----|------|------------------------|--------------|------|\n")
            for i, c in enumerate(candidates, 1):
                f.write(
                    f"| {i} | `{c['category']}` | {len(c['related'])} | {c['loc']} | {c['kind']} | {c['why']} | `{c['fqn']}` | {c['file']} |\n"
                )

            detailed_candidates = candidates[:20]
            if detailed_candidates:
                f.write("\n## Why These Candidates Surfaced\n\n")
                f.write("> [!NOTE]\n")
                f.write("> The notes below summarize which structural signals were missing and whether RepoGraph found nearby family members.\n\n")
                for candidate in detailed_candidates:
                    f.write(f"<details><summary>`{candidate['fqn']}`</summary>\n\n")
                    f.write(f"- category: `{candidate['category']}`\n")
                    f.write(f"- why: {candidate['why']}\n")
                    signal_summary = self._format_signal_summary(candidate["signals"])
                    if signal_summary:
                        f.write(f"- structural signals: {signal_summary}\n")
                    if candidate["related"]:
                        f.write("- nearby symbols:\n")
                        for related in candidate["related"]:
                            reason_text = ", ".join(related["reasons"][:4])
                            line = f"  - `{related['fqn']}` ({related['kind']}, score {related['score']:.4f})"
                            if reason_text:
                                line += f" — {reason_text}"
                            f.write(line + "\n")
                    else:
                        f.write("- nearby symbols: none above the similarity threshold\n")
                    f.write("\n</details>\n\n")

            annotated_candidates = [candidate for candidate in candidates[:20] if candidate["related"]]
            if annotated_candidates:
                f.write("## Related Existing Implementations\n\n")
                f.write("> [!TIP]\n")
                f.write("> These candidates have nearby existing symbols with similar naming or structural context.\n")
                f.write("> Use this section to check whether the candidate is actually a duplicate family member, a callback slot, or a refactoring target rather than dead code.\n\n")
                for candidate in annotated_candidates:
                    f.write(f"<details><summary>`{candidate['fqn']}`</summary>\n\n")
                    for related in candidate["related"]:
                        f.write(f"- `{related['fqn']}` ({related['kind']}, score {related['score']:.4f})\n")
                        reason_text = ", ".join(related["reasons"][:4])
                        if reason_text:
                            f.write(f"  reasons: {reason_text}\n")
                    f.write("\n</details>\n\n")

        with open(json_path, "w", encoding="utf-8") as f:
            import json
            json.dump({"candidates": candidates}, f, indent=2, ensure_ascii=False)
                
        logger.info(f"Generated dead code report with {len(candidates)} candidates at {report_path}")

    def _attach_related_candidates(self, candidates: List[Dict]) -> None:
        for candidate in candidates[:50]:
            symbol = self.related_finder.find_symbol(candidate["fqn"])
            if symbol is None:
                continue
            related = self.related_finder.find_related(symbol, top_k=3)
            candidate["related"] = [item.to_dict() for item in related if item.score >= 0.45]

    def _attach_explanations(self, candidates: List[Dict]) -> None:
        for candidate in candidates:
            candidate["why"] = self._build_candidate_explanation(candidate)

    @staticmethod
    def _categorize_candidates(candidates: List[Dict]) -> None:
        for candidate in candidates:
            candidate["category"] = DeadCodeDetector._classify_candidate(candidate)

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
        }

        if sym.containing_type:
            signals["containing_type_users"] = self._graph_predecessor_count("type_dependency_graph", sym.containing_type)

        return signals

    def _build_candidate_explanation(self, candidate: Dict) -> str:
        signals = candidate["signals"]
        missing = []
        if signals.get("callers", 0) == 0:
            missing.append("no callers")
        if signals.get("type_users", 0) == 0:
            missing.append("no type users")
        if signals.get("field_users", 0) == 0 and candidate["kind"] in {"field", "property"}:
            missing.append("no field readers/writers")
        if candidate["kind"] in {"class", "interface"} and signals.get("derived_types", 0) == 0:
            missing.append("no derived types")
        if candidate["kind"] == "constructor" and signals.get("containing_type_users", 0) == 0:
            missing.append("owning type has no detected type users")

        if not missing:
            missing.append("no inbound structural references")

        if candidate["related"]:
            strongest = candidate["related"][0]
            reason_text = ", ".join(strongest.get("reasons", [])[:2])
            if reason_text:
                return f"{'; '.join(missing)}; nearest sibling looks like {reason_text}"
            return f"{'; '.join(missing)}; has nearby structural sibling"

        return "; ".join(missing)

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

    @staticmethod
    def _looks_like_ui_type(sym: Symbol) -> bool:
        name = sym.name or ""
        file_name = sym.document.file_name if sym.document else ""

        if DeadCodeDetector._looks_like_ui_type_name(name):
            return True

        if ".Designer." in file_name:
            return True

        return False

    @staticmethod
    def _looks_like_framework_convention_type(sym: Symbol) -> bool:
        name = (sym.name or "").split(".")[-1]
        return name in {"App", "Program", "Startup"} or name.endswith((
            "Module", "Converter", "Behavior", "Extension", "Extensions", "ExtensionMethods",
            "Controller", "Middleware", "ActionFilter"
        ))

    @staticmethod
    def _looks_like_ui_type_name(name: str) -> bool:
        ui_suffixes = (
            "Form", "Control", "Dialog", "Window", "Page",
            "View", "Panel", "Editor"
        )
        short_name = name.split(".")[-1] if name else ""
        return bool(short_name) and (
            short_name.endswith(ui_suffixes)
            or short_name.startswith("Form")
        )

    @staticmethod
    def _looks_like_framework_convention_method(sym: Symbol) -> bool:
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()
        file_path = sym.document.file_path.lower() if sym.document and sym.document.file_path else ""
        callback_names = {
            "render", "drawitem", "ondrawitem", "onrender", "onupdate",
            "onpointerpressed", "onpointermoved", "onpointerreleased",
            "onkeydown", "onkeyup", "ontextinput", "onloaded",
            "onselectionchanged", "onviewmodelpropertychanged",
        }

        if name in {
            "onstartup", "oninitialize", "onactivated", "onapplytemplate",
            "onframeworkinitializationcompleted",
            "convert", "convertback", "load",
            "configure", "configureservices",
            "onactionexecuting", "onactionexecuted"
        }:
            return True

        if name.startswith("handle") and containing_type.endswith(("behavior", "adapter")):
            return True

        if name.endswith("propertychanged") and "(dependencyobject, dependencypropertychangedeventargs)" in (sym.fqn or "").lower():
            return True

        if name == "save" and containing_type.endswith("viewmodel"):
            return True

        if (file_path.endswith(".axaml.cs") or file_path.endswith(".xaml.cs")) and name.startswith("on"):
            return True

        if name in callback_names:
            return True

        if name in {"render", "drawitem", "onupdate"} and containing_type.endswith((
            "renderer", "shape", "annotation", "control", "tool"
        )):
            return True

        return False

    def _looks_like_message_recipient_method(self, sym: Symbol) -> bool:
        name = (sym.name or "").lower()
        fqn_lower = (sym.fqn or "").lower()
        if name != "receive":
            return False

        if ".messages." in fqn_lower:
            return True

        if not sym.containing_type:
            return False

        siblings = self._symbols_by_containing_type.get(sym.containing_type, [])
        receive_methods = [member for member in siblings if member.kind == "method" and (member.name or "").lower() == "receive"]
        return len(receive_methods) >= 2

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
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()
        path_lower = file_path.lower()

        if name in {"main", "mainasync"}:
            return True

        if name in {"invoke", "invokeasync"} and (
            containing_type.endswith("middleware")
            or "\\middleware\\" in path_lower
        ):
            return True

        if name.startswith("use") and containing_type.endswith(("extensions", "middleware")):
            return True

        if name in {"configure", "configureservices"} and containing_type == "startup":
            return True

        if name in {"onactionexecuting", "onactionexecuted"} and (
            containing_type.endswith("filter")
            or "\\controllers\\" in path_lower
            or "\\filters\\" in path_lower
        ):
            return True

        if "\\controllers\\" in path_lower and containing_type.endswith("controller"):
            if (sym.accessibility or "").lower() == "public":
                return True

        if name == "map" and (
            containing_type.endswith(("endpoint", "endpoints", "extensions"))
            or "\\webservercore\\endpoints\\" in path_lower
        ):
            return True

        if name.startswith("create") and containing_type.endswith(("webserver", "host", "builder")):
            return True

        return False
    def _type_has_live_members(self, sym: Symbol) -> bool:
        members = self._symbols_by_containing_type.get(sym.fqn, [])
        for member in members:
            if member.fan_in and member.fan_in > 0:
                return True

            if self._has_graph_predecessors(member.fqn):
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

    def _has_graph_predecessors(self, fqn: str) -> bool:
        graph_names = ("call_graph", "inheritance_graph", "type_dependency_graph", "field_access_graph")
        for graph_name in graph_names:
            graph = getattr(self.graph, graph_name, None)
            if graph is None or fqn not in graph:
                continue

            try:
                if any(True for _ in graph.predecessors(fqn)):
                    return True
            except Exception:
                continue

        return False

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
