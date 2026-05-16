import os
from typing import List, Dict
from sqlalchemy.orm import Session
from models import Symbol
from graph import GraphLoader
from loguru import logger

class DeadCodeDetector:
    def __init__(self, session: Session, graph_loader: GraphLoader, reports_dir: str):
        self.session = session
        self.graph = graph_loader
        self.reports_dir = reports_dir
        os.makedirs(self.reports_dir, exist_ok=True)

    def detect_dead_code_candidates(self) -> List[Dict]:
        candidates = []
        # fan_in == 0 のシンボルを取得（明示的なメソッド呼び出しがないもの）
        symbols = self.session.query(Symbol).filter(Symbol.fan_in == 0).all()
        
        for sym in symbols:
            fqn = sym.fqn
            fqn_lower = fqn.lower()
            
            # 1. 型依存関係グラフ（Type Dependency）によるセーフティネット
            if hasattr(self.graph, 'type_dependency_graph') and fqn in self.graph.type_dependency_graph:
                type_users = list(self.graph.type_dependency_graph.predecessors(fqn))
                if len(type_users) > 0:
                    continue # キャストやジェネリクス等で型として使われているため除外
                    
            # 2. 継承グラフ（Inheritance）によるセーフティネット
            if sym.kind in ["class", "interface"]:
                if fqn in self.graph.inheritance_graph:
                    derived_classes = list(self.graph.inheritance_graph.predecessors(fqn))
                    if len(derived_classes) > 0:
                        continue # 派生クラスが存在するため基底として機能しているとみなし除外
            
            # 3. ヒューリスティックな暗黙的呼び出しのノイズ除外
            if "main(" in fqn_lower: 
                continue
            if sym.kind == "method":
                # 一般的なUIイベントハンドラのサフィックス
                if any(evt in fqn_lower for evt in ["_click(", "_load(", "_changed(", "_tick("]):
                    continue
                # 一般的なフレームワークライフサイクル
                if any(sys in fqn_lower for sys in [".dispose(", ".wndproc(", ".onpaint("]):
                    continue
                    
            candidates.append({
                "fqn": sym.fqn,
                "kind": sym.kind,
                "loc": sym.loc if sym.loc else 0,
                "file": sym.document.file_name if sym.document else ""
            })
            
        # LOC（コード行数）が大きい＝削除時のリターンが大きい順にソート
        candidates.sort(key=lambda x: x["loc"], reverse=True)
        return candidates

    def generate_report(self):
        candidates = self.detect_dead_code_candidates()
        report_path = os.path.join(self.reports_dir, "dead_code_candidates.md")
        
        with open(report_path, "w", encoding="utf-8") as f:
            f.write("# Dead Code Candidates\n\n")
            f.write("> **⚠️ Warning**: These are heuristic candidates based on structural graph analysis (Fan-in = 0, No Derived Classes, No Type Usages).\n")
            f.write("> **Always verify with AI text search (grep) or IDE references before deletion.**\n\n")
            f.write("| Rank | LOC | Kind | Symbol (FQN) | File |\n")
            f.write("|------|-----|------|--------------|------|\n")
            for i, c in enumerate(candidates, 1):
                f.write(f"| {i} | {c['loc']} | {c['kind']} | `{c['fqn']}` | {c['file']} |\n")
                
        logger.info(f"Generated dead code report with {len(candidates)} candidates at {report_path}")
