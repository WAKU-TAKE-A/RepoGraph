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

            # 0.5. 動的ローダーや framework convention による登録対象は、deadcode 候補としての信頼度が低い
            if self._looks_like_dynamic_module(sym, file_path):
                continue

            if self._looks_like_discovered_endpoint(sym, file_path):
                continue

            if self._looks_like_reflection_discovered_implementation(sym, file_path):
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
                if self._looks_like_framework_convention_method(sym):
                    continue

                if self._looks_like_host_integration_method(sym, file_path):
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
        candidates = self._prioritize_candidates(candidates)
        report_path = os.path.join(self.reports_dir, "dead_code_candidates.md")
        
        with open(report_path, "w", encoding="utf-8") as f:
            f.write("# Dead Code Candidates\n\n")
            f.write("> **⚠️ Warning**: These are heuristic candidates based on structural graph analysis (Fan-in = 0, No Derived Classes, No Type Usages).\n")
            f.write("> **Always verify with AI text search (grep) or IDE references before deletion.**\n\n")
            category_counts = self._summarize_categories(candidates)
            f.write("## Investigation Categories\n\n")
            f.write(f"- `isolated`: {category_counts['isolated']} candidates with weak or no nearby matches\n")
            f.write(f"- `structural-sibling`: {category_counts['structural-sibling']} candidates with some structural similarity nearby\n")
            f.write(f"- `near-family`: {category_counts['near-family']} candidates that strongly resemble existing family members or variants\n\n")
            f.write("| Rank | Category | Related | LOC | Kind | Symbol (FQN) | File |\n")
            f.write("|------|----------|---------|-----|------|--------------|------|\n")
            for i, c in enumerate(candidates, 1):
                f.write(
                    f"| {i} | `{c['category']}` | {len(c['related'])} | {c['loc']} | {c['kind']} | `{c['fqn']}` | {c['file']} |\n"
                )

            annotated_candidates = [candidate for candidate in candidates[:20] if candidate["related"]]
            if annotated_candidates:
                f.write("\n## Related Existing Implementations\n\n")
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
                
        logger.info(f"Generated dead code report with {len(candidates)} candidates at {report_path}")

    def _attach_related_candidates(self, candidates: List[Dict]) -> None:
        for candidate in candidates[:50]:
            symbol = self.related_finder.find_symbol(candidate["fqn"])
            if symbol is None:
                continue
            related = self.related_finder.find_related(symbol, top_k=3)
            candidate["related"] = [item.to_dict() for item in related if item.score >= 0.45]

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
        return name in {"App", "Program"} or name.endswith((
            "Module", "Converter", "Behavior", "Extension", "Extensions", "ExtensionMethods"
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
            "convert", "convertback", "load"
        }:
            return True

        if name.startswith("handle") and containing_type.endswith(("behavior", "adapter")):
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
    def _looks_like_dynamic_module(sym: Symbol, file_path: str) -> bool:
        combined = f"{sym.fqn} {file_path}".lower()
        namespace_markers = (
            ".library.backend.",
            ".library.modules.",
            ".library.secretprovider.",
            ".library.sourceprovider.",
            ".library.sourceproviders.",
            ".library.encryption.",
            ".library.compression.",
            ".library.windowsmodules.",
            ".library.dynamicloader.",
        )
        path_markers = (
            "\\library\\backend\\",
            "\\library\\modules\\",
            "\\library\\secretprovider\\",
            "\\library\\sourceprovider\\",
            "\\library\\sourceproviders\\",
            "\\library\\encryption\\",
            "\\library\\compression\\",
            "\\library\\windowsmodules\\",
            "\\library\\dynamicloader\\",
        )
        type_suffixes = (
            "backend", "secretprovider", "sourceprovider", "restoredestinationprovider",
            "encryption", "compression", "modules", "loader",
        )

        if any(marker in combined for marker in namespace_markers + path_markers):
            return True

        short_name = (sym.name or "").split(".")[-1].lower()
        return any(short_name.endswith(suffix) for suffix in type_suffixes)

    @staticmethod
    def _looks_like_discovered_endpoint(sym: Symbol, file_path: str) -> bool:
        combined = f"{sym.fqn} {file_path}".lower()
        if "\\webservercore\\endpoints\\" in combined:
            return True

        short_name = (sym.name or "").split(".")[-1]
        if short_name in {"Auth", "Filesystem", "ConnectionStrings", "DestinationVerify"}:
            return "\\webservercore\\" in combined

        return False

    @staticmethod
    def _looks_like_host_integration_method(sym: Symbol, file_path: str) -> bool:
        name = (sym.name or "").lower()
        containing_type = (sym.containing_type or "").split(".")[-1].lower()
        path_lower = file_path.lower()

        if name in {"main", "mainasync"}:
            return True

        if name in {"invoke", "invokeasync"} and containing_type.endswith("middleware"):
            return True

        if name.startswith("use") and containing_type.endswith(("extensions", "middleware")):
            return True

        if name == "map" and (
            containing_type.endswith(("endpoint", "endpoints", "extensions"))
            or "\\webservercore\\endpoints\\" in path_lower
        ):
            return True

        if name.startswith("create") and containing_type.endswith(("webserver", "host", "builder")):
            return True

        return False

    def _looks_like_reflection_discovered_implementation(self, sym: Symbol, file_path: str) -> bool:
        if sym.kind != "class":
            return False

        fqn = sym.fqn
        short_name = (sym.name or "").split(".")[-1]
        file_lower = file_path.lower()

        reflection_base_suffixes = (
            "imageeffectbase",
            "imageuploader",
            "fileuploader",
            "textuploader",
        )
        reflection_path_markers = (
            "\\imageeffects\\",
            "\\imageuploaders\\",
            "\\fileuploaders\\",
            "\\textuploaders\\",
        )
        reflection_name_suffixes = (
            "imageeffect",
            "uploader",
        )

        if hasattr(self.graph, "inheritance_graph") and fqn in self.graph.inheritance_graph:
            try:
                for base_fqn in self.graph.inheritance_graph.successors(fqn):
                    base_short_name = base_fqn.split(".")[-1].lower()
                    if any(base_short_name.endswith(suffix) for suffix in reflection_base_suffixes):
                        return True
            except Exception:
                pass

        if any(marker in file_lower for marker in reflection_path_markers):
            if any(short_name.lower().endswith(suffix) for suffix in reflection_name_suffixes):
                return True

        return False

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
