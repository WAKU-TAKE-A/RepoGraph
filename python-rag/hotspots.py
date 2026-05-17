import os
import json
from typing import List, Dict, Any, Set
from sqlalchemy.orm import Session
from models import Symbol, FieldAccess, Project
from graph import GraphLoader
from loguru import logger

def _categorize_writer(fqn: str) -> str:
    fqn_lower = fqn.lower()
    if any(kw in fqn_lower for kw in [".ctor", "_load", "initui", "initialize", "_shown", "setparamdefault"]):
        return "init"
    if any(kw in fqn_lower for kw in ["_click", "_checkedchanged", "_selectedindexchanged", "_scroll", "_propvaluechanged", "_domainchanged", "_formclosed", "_formclosing"]):
        return "event"
    return "runtime"

class HotspotScorer:
    def __init__(self, session: Session, graph_loader: GraphLoader, output_dir: str):
        self.session = session
        self.graph = graph_loader
        self.output_dir = output_dir
        os.makedirs(self.output_dir, exist_ok=True)
        self._projects = self.session.query(Project).all()
        self._project_name_by_id = {project.id: project.name for project in self._projects}
        self._test_project_ids = {project.id for project in self._projects if project.is_test_project}
        self._symbol_project_ids = {
            fqn: project_id
            for fqn, project_id in self.session.query(Symbol.fqn, Symbol.project_id).all()
        }
        
        # Default weights
        self.weights = {
            "fan_in": 0.25,
            "fan_out": 0.20,
            "loc": 0.20,
            "parameter_count": 0.10,
            "async_flag": 0.10,
            "static_coupling": 0.15
        }

    def compute_hotspots(self) -> List[Dict[str, Any]]:
        symbols = self.session.query(Symbol).all()
        logger.info(f"Computing hotspots for {len(symbols)} symbols...")
        raw_metrics = []

        # 1. Gather raw metrics
        symbols_with_metrics = []
        for symbol in symbols:
            fan_in = self.graph.call_graph.in_degree(symbol.fqn) if symbol.fqn in self.graph.call_graph else 0
            fan_out = self.graph.call_graph.out_degree(symbol.fqn) if symbol.fqn in self.graph.call_graph else 0
            project_name = self._project_name_by_id.get(symbol.project_id, "")
            static_coupling = self.graph.dependency_graph.degree(project_name) if project_name and project_name in self.graph.dependency_graph else 0
            
            symbols_with_metrics.append({
                "symbol": symbol,
                "fan_in": fan_in,
                "fan_out": fan_out,
                "static_coupling": static_coupling,
                "project_name": project_name,
                "is_test_project": self._is_test_symbol(symbol),
            })

        # 1b. Aggregate member Fan-In into Classes
        class_metrics = {}
        for item in symbols_with_metrics:
            s = item["symbol"]
            if s.kind == "class":
                class_metrics[s.fqn] = item

        for item in symbols_with_metrics:
            s = item["symbol"]
            if s.containing_type and s.containing_type in class_metrics:
                # Add member's usage to the class's total usage
                class_metrics[s.containing_type]["fan_in"] += item["fan_in"]

        for item in symbols_with_metrics:
            s = item["symbol"]
            file_name = os.path.basename(s.document.file_path) if s.document and s.document.file_path else ""
            m = {
                "fan_in": item["fan_in"],
                "fan_out": item["fan_out"],
                "loc": s.loc or 0,
                "parameter_count": s.parameter_count or 0,
                "async_flag": 1 if s.is_async else 0,
                "static_coupling": item["static_coupling"]
            }
            
            raw_metrics.append({
                "fqn": s.fqn,
                "kind": s.kind,
                "file_name": file_name,
                "line_start": s.line_start or 0,
                "metrics": m,
                "has_ui_dispatch": bool(s.has_ui_dispatch),
                "has_task_spawn": bool(s.has_task_spawn),
                "has_background_worker": bool(s.has_background_worker),
                "has_do_events": bool(s.has_do_events),
                "has_lock": bool(s.has_lock),
                "has_callback": bool(s.has_callback),
                "has_thread_start": bool(s.has_thread_start),
                "has_blocking_wait": bool(s.has_blocking_wait),
                "project_name": item["project_name"],
                "is_test_project": item["is_test_project"],
            })
            
        if not raw_metrics:
            return []

        # 2. Normalize metrics (Min-Max normalization)
        def get_max(key):
            return max(r["metrics"][key] for r in raw_metrics) or 1.0 # avoid div by zero

        max_fan_in = get_max("fan_in")
        max_fan_out = get_max("fan_out")
        max_loc = get_max("loc")
        max_params = get_max("parameter_count")
        max_static = get_max("static_coupling")
        
        # 3. Compute score
        scored_results = []
        for r in raw_metrics:
            m = r["metrics"]
            score = (
                self.weights["fan_in"] * (m["fan_in"] / max_fan_in) +
                self.weights["fan_out"] * (m["fan_out"] / max_fan_out) +
                self.weights["loc"] * (m["loc"] / max_loc) +
                self.weights["parameter_count"] * (m["parameter_count"] / max_params) +
                self.weights["async_flag"] * m["async_flag"] + # binary already
                self.weights["static_coupling"] * (m["static_coupling"] / max_static)
            )
            
            danger_score = m["fan_in"] * m["loc"]
            
            is_anti_pattern = False
            if r["kind"] == "class":
                name_lower = r["fqn"].lower()
                has_smelly_name = any(kw in name_lower for kw in ["manager", "controller", "global", "station", "base", "editor"])
                is_anti_pattern = (
                    danger_score >= 2000 or
                    (m["loc"] >= 1500 and m["fan_in"] >= 5) or
                    (has_smelly_name and danger_score >= 500)
                )

            # Threading hazard detection
            is_threading_hazard = (
                r["has_do_events"] and (r["has_ui_dispatch"] or r["has_background_worker"] or r["has_task_spawn"])
            ) or (
                r["has_ui_dispatch"] and (r["has_task_spawn"] or r["has_background_worker"] or r["has_thread_start"] or r["has_blocking_wait"])
            )

            scored_results.append({
                "fqn": r["fqn"],
                "kind": r["kind"],
                "file_name": r["file_name"],
                "line_start": r["line_start"],
                "score": round(score, 4),
                "danger_score": danger_score,
                "is_anti_pattern": is_anti_pattern,
                "is_threading_hazard": is_threading_hazard,
                "thread_flags": {
                    "has_ui_dispatch": r["has_ui_dispatch"],
                    "has_task_spawn": r["has_task_spawn"],
                    "has_background_worker": r["has_background_worker"],
                    "has_do_events": r["has_do_events"],
                    "has_lock": r["has_lock"],
                    "has_callback": r["has_callback"],
                    "has_thread_start": r["has_thread_start"],
                    "has_blocking_wait": r["has_blocking_wait"],
                },
                "metrics": m,
                "project_name": r["project_name"],
                "is_test_project": r["is_test_project"],
            })
            
        # 4. Sort by score descending
        scored_results.sort(key=lambda x: x["score"], reverse=True)
        return scored_results

    def compute_shared_mutable_state(self) -> List[Dict[str, Any]]:
        """
        Find fields/properties that are written by multiple methods (especially across classes).
        These are "shared mutable state" hotspots that cause race conditions and implicit coupling.
        """
        field_accesses = self.session.query(FieldAccess).all()
        owned_targets: Set[str] = {
            fqn for (fqn,) in self.session.query(Symbol.fqn)
            .filter(Symbol.kind.in_(("field", "property")))
            .all()
        }
        if not field_accesses:
            logger.info("No field accesses found.")
            return []
        if not owned_targets:
            logger.warning(
                "Field accesses exist (%d rows), but no owned field/property symbols were loaded.",
                len(field_accesses),
            )
            return []

        # Group by target field
        target_writers: Dict[str, Dict[str, Any]] = {}
        owned_access_count = 0
        for fa in field_accesses:
            if fa.target_fqn not in owned_targets:
                continue
            owned_access_count += 1

            if fa.target_fqn not in target_writers:
                target_writers[fa.target_fqn] = {
                    "writers": set(),
                    "external_writers": set(),
                    "internal_writers": set(),
                    "readers": set(),
                    "init_writers": set(),
                    "event_writers": set(),
                    "runtime_writers": set(),
                }

            if fa.access_kind in ("write", "read_write"):
                target_writers[fa.target_fqn]["writers"].add(fa.accessor_fqn)
                category = _categorize_writer(fa.accessor_fqn)
                target_writers[fa.target_fqn][f"{category}_writers"].add(fa.accessor_fqn)

                if fa.is_external:
                    target_writers[fa.target_fqn]["external_writers"].add(fa.accessor_fqn)
                else:
                    target_writers[fa.target_fqn]["internal_writers"].add(fa.accessor_fqn)

            if fa.access_kind in ("read", "read_write"):
                target_writers[fa.target_fqn]["readers"].add(fa.accessor_fqn)

        if owned_access_count == 0:
            logger.warning(
                "Field accesses exist (%d rows), but none target owned field/property symbols.",
                len(field_accesses),
            )
            return []

        # Find fields with multiple writers (dangerous shared state)
        shared_state = []
        for target_fqn, info in target_writers.items():
            target_project_id = self._symbol_project_ids.get(target_fqn)
            if target_project_id in self._test_project_ids or self._looks_like_test_target(target_fqn):
                continue

            writer_count = len(info["writers"])
            external_writer_count = len(info["external_writers"])
            reader_count = len(info["readers"])
            init_count = len(info["init_writers"])
            event_count = len(info["event_writers"])
            runtime_count = len(info["runtime_writers"])

            # Classify the spaghetti type
            spaghetti_type = "none"
            if writer_count >= 2:
                if external_writer_count >= 2:
                    spaghetti_type = "external"  # Cross-class mutation (most dangerous)
                elif external_writer_count >= 1:
                    spaghetti_type = "mixed"     # Some internal, some external
                else:
                    spaghetti_type = "internal"  # Multiple methods in same class

            if writer_count >= 2:
                risk_score = (runtime_count * 10) + (event_count * 3) + (init_count * 0.1)
                
                shared_state.append({
                    "target_fqn": target_fqn,
                    "writer_count": writer_count,
                    "external_writer_count": external_writer_count,
                    "internal_writer_count": len(info["internal_writers"]),
                    "reader_count": reader_count,
                    "init_writer_count": init_count,
                    "event_writer_count": event_count,
                    "runtime_writer_count": runtime_count,
                    "risk_score": risk_score,
                    "spaghetti_type": spaghetti_type,
                    "writers": sorted(info["writers"]),
                    "external_writers": sorted(info["external_writers"]),
                    "init_writers": sorted(info["init_writers"]),
                    "event_writers": sorted(info["event_writers"]),
                    "runtime_writers": sorted(info["runtime_writers"]),
                })

        # Sort by risk_score descending
        shared_state.sort(key=lambda x: (x["risk_score"], x["writer_count"]), reverse=True)
        if not shared_state:
            logger.info(
                "Processed %d owned field/property accesses across %d targets, but no multi-writer shared state was found.",
                owned_access_count,
                len(target_writers),
            )
        return shared_state

    def _is_test_symbol(self, symbol: Symbol) -> bool:
        if symbol.project_id in self._test_project_ids:
            return True

        file_path = symbol.document.file_path if symbol.document and symbol.document.file_path else ""
        combined = f"{symbol.fqn} {file_path}".lower()
        markers = (
            ".unittest.", ".tests.", ".test.",
            "\\unittest\\", "\\tests\\", "\\test\\",
            "\\integrationtests\\", "\\livetests\\", "\\browser.tests\\",
        )
        return any(marker in combined for marker in markers)

    @staticmethod
    def _looks_like_test_target(target_fqn: str) -> bool:
        target_lower = target_fqn.lower()
        return any(marker in target_lower for marker in (
            ".unittest.", ".tests.", ".test."
        ))

    def generate_reports(self):
        hotspots = self.compute_hotspots()
        shared_state = self.compute_shared_mutable_state()
        
        if not hotspots and not shared_state:
            logger.warning("No hotspots computed.")
            return

        json_path = os.path.join(self.output_dir, "hotspots.json")
        md_path = os.path.join(self.output_dir, "hotspots.md")
        
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump({
                "hotspots": hotspots,
                "shared_mutable_state": shared_state,
            }, f, indent=2, default=str)
            
        with open(md_path, "w", encoding="utf-8") as f:
            f.write("# Repository Hotspots\n\n")
            
            # --- Anti-Pattern Warnings (God Class) ---
            anti_patterns = [h for h in hotspots if h["is_anti_pattern"] and not h["is_test_project"]]
            if anti_patterns:
                f.write("## ⚠️ Anti-Pattern Warnings (God Class / Service Locator)\n\n")
                f.write("> [!WARNING]\n")
                f.write("> The following classes have unusually high 'Danger Scores' (Fan-In × LOC) and names suggesting they might be managing global state or acting as God Classes.\n\n")
                f.write("| Rank | Danger Score | Symbol (FQN) | LOC | Fan-in |\n")
                f.write("|------|--------------|--------------|-----|--------|\n")
                for i, h in enumerate(anti_patterns[:20]):
                    m = h["metrics"]
                    link = f"[`{h['fqn']}`]({h['file_name']}#L{h['line_start']})" if h['file_name'] else f"`{h['fqn']}`"
                    f.write(f"| {i+1} | {h['danger_score']} | {link} | {m['loc']} | {m['fan_in']} |\n")
                f.write("\n---\n\n")

            # --- Threading Hazard Warnings ---
            threading_hazards = [h for h in hotspots if h["is_threading_hazard"] and not h["is_test_project"]]
            if threading_hazards:
                f.write("## 🔴 Threading Hazard Warnings\n\n")
                f.write("> [!CAUTION]\n")
                f.write("> The following methods mix UI thread dispatch with background work or use `Application.DoEvents()`, creating re-entrancy and thread safety risks.\n\n")
                f.write("| # | Symbol (FQN) | UI Dispatch | Task Spawn | BgWorker | Thread Start | Blocking Wait | DoEvents | Lock |\n")
                f.write("|---|--------------|-------------|------------|----------|--------------|---------------|----------|------|\n")
                for i, h in enumerate(threading_hazards[:30]):
                    tf = h["thread_flags"]
                    link = f"[`{h['fqn']}`]({h['file_name']}#L{h['line_start']})" if h['file_name'] else f"`{h['fqn']}`"
                    f.write(f"| {i+1} | {link} | {'✅' if tf['has_ui_dispatch'] else '—'} | {'✅' if tf['has_task_spawn'] else '—'} | {'✅' if tf['has_background_worker'] else '—'} | {'✅' if tf['has_thread_start'] else '—'} | {'⏳' if tf['has_blocking_wait'] else '—'} | {'⚠️' if tf['has_do_events'] else '—'} | {'🔒' if tf['has_lock'] else '—'} |\n")
                f.write("\n---\n\n")

            # --- Shared Mutable State Warnings ---
            if shared_state:
                # High Risk: Has Runtime or Event writers
                high_risk = [s for s in shared_state if s["runtime_writer_count"] > 0 or s["event_writer_count"] > 0]
                # Low Risk: Only Init writers
                low_risk = [s for s in shared_state if s["runtime_writer_count"] == 0 and s["event_writer_count"] == 0]

                if high_risk:
                    f.write("## 🔴 Shared Mutable State (Runtime/Event — High Risk)\n\n")
                    f.write("> [!CAUTION]\n")
                    f.write("> The following fields are mutated during **runtime execution or user events** across multiple methods. This indicates dangerous implicit coupling and race condition risks.\n\n")
                    f.write("| # | Field | Risk Score | Runtime | Event | Init | Readers | Type |\n")
                    f.write("|---|-------|------------|---------|-------|------|---------|------|\n")
                    for i, s in enumerate(high_risk[:40]):
                        f.write(f"| {i+1} | `{s['target_fqn']}` | {s['risk_score']:.1f} | {s['runtime_writer_count']} | {s['event_writer_count']} | {s['init_writer_count']} | {s['reader_count']} | {s['spaghetti_type']} |\n")
                    f.write("\n")

                    # Show details for top high-risk
                    for s in high_risk[:5]:
                        f.write(f"<details><summary>📝 {s['target_fqn']}</summary>\n\n")
                        if s["runtime_writers"]:
                            f.write("**Runtime writers:**\n")
                            for w in s["runtime_writers"]:
                                f.write(f"- `{w}`\n")
                        if s["event_writers"]:
                            f.write("**Event writers:**\n")
                            for w in s["event_writers"]:
                                f.write(f"- `{w}`\n")
                        f.write("\n</details>\n\n")

                    f.write("---\n\n")

                if low_risk:
                    f.write("## ℹ️ Shared Mutable State (Init-only — Low Risk Wiring)\n\n")
                    f.write("> [!NOTE]\n")
                    f.write("> The following fields have multiple writers, but **only during initialization** (e.g., `_Load`, `InitUI`). These are likely just UI data binding or configuration wiring.\n\n")
                    f.write("| # | Field | Init Writers | Readers | Type |\n")
                    f.write("|---|-------|--------------|---------|------|\n")
                    for i, s in enumerate(low_risk[:20]):
                        f.write(f"| {i+1} | `{s['target_fqn']}` | {s['init_writer_count']} | {s['reader_count']} | {s['spaghetti_type']} |\n")
                    f.write("\n---\n\n")

            # --- General Hotspots ---
            f.write("## Top 50 Hotspots (General)\n\n")
            f.write("| Rank | Score | Kind | Symbol (FQN) | LOC | Fan-in | Fan-out |\n")
            f.write("|------|-------|------|--------------|-----|--------|----------|\n")
            
            ranked_hotspots = [h for h in hotspots if not h["is_test_project"]]
            for i, h in enumerate(ranked_hotspots[:50]): # Top 50
                m = h["metrics"]
                link = f"[`{h['fqn']}`]({h['file_name']}#L{h['line_start']})" if h['file_name'] else f"`{h['fqn']}`"
                f.write(f"| {i+1} | {h['score']:.4f} | {h['kind']} | {link} | {m['loc']} | {m['fan_in']} | {m['fan_out']} |\n")

        logger.info(f"Generated hotspot reports: {json_path}, {md_path}")
        if shared_state:
            high_risk_count = len([s for s in shared_state if s["runtime_writer_count"] > 0 or s["event_writer_count"] > 0])
            low_risk_count = len([s for s in shared_state if s["runtime_writer_count"] == 0 and s["event_writer_count"] == 0])
            logger.info(f"Shared mutable state: {high_risk_count} high risk (runtime/event), {low_risk_count} low risk (init-only)")
