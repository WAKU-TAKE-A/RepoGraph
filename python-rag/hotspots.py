import os
import json
from typing import List, Dict, Any, Set
from sqlalchemy.orm import Session
from models import Symbol, FieldAccess
from graph import GraphLoader
from loguru import logger

class HotspotScorer:
    def __init__(self, session: Session, graph_loader: GraphLoader, output_dir: str):
        self.session = session
        self.graph = graph_loader
        self.output_dir = output_dir
        os.makedirs(self.output_dir, exist_ok=True)
        
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
            static_coupling = self.graph.dependency_graph.degree(symbol.fqn) if symbol.fqn in self.graph.dependency_graph else 0
            
            symbols_with_metrics.append({
                "symbol": symbol,
                "fan_in": fan_in,
                "fan_out": fan_out,
                "static_coupling": static_coupling
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
                # Flag based on combined danger score or extreme size
                if danger_score > 500 or m["loc"] > 2000:
                    name_lower = r["fqn"].lower()
                    # Focus on "Manager", "Station", "Controller", etc.
                    if any(kw in name_lower for kw in ["manager", "controller", "global", "station", "base"]):
                        is_anti_pattern = True

            # Threading hazard detection
            is_threading_hazard = (
                r["has_do_events"] and (r["has_ui_dispatch"] or r["has_background_worker"])
            ) or (
                r["has_ui_dispatch"] and r["has_task_spawn"]
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
                },
                "metrics": m
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
        if not field_accesses:
            logger.info("No field accesses found.")
            return []

        # Group by target field
        target_writers: Dict[str, Dict[str, Any]] = {}
        for fa in field_accesses:
            if fa.access_kind in ("write", "read_write"):
                if fa.target_fqn not in target_writers:
                    target_writers[fa.target_fqn] = {
                        "writers": set(),
                        "external_writers": set(),
                        "internal_writers": set(),
                        "readers": set(),
                    }
                target_writers[fa.target_fqn]["writers"].add(fa.accessor_fqn)
                if fa.is_external:
                    target_writers[fa.target_fqn]["external_writers"].add(fa.accessor_fqn)
                else:
                    target_writers[fa.target_fqn]["internal_writers"].add(fa.accessor_fqn)

            if fa.access_kind in ("read", "read_write"):
                if fa.target_fqn not in target_writers:
                    target_writers[fa.target_fqn] = {
                        "writers": set(),
                        "external_writers": set(),
                        "internal_writers": set(),
                        "readers": set(),
                    }
                target_writers[fa.target_fqn]["readers"].add(fa.accessor_fqn)

        # Find fields with multiple writers (dangerous shared state)
        shared_state = []
        for target_fqn, info in target_writers.items():
            writer_count = len(info["writers"])
            external_writer_count = len(info["external_writers"])
            reader_count = len(info["readers"])

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
                shared_state.append({
                    "target_fqn": target_fqn,
                    "writer_count": writer_count,
                    "external_writer_count": external_writer_count,
                    "internal_writer_count": len(info["internal_writers"]),
                    "reader_count": reader_count,
                    "spaghetti_type": spaghetti_type,
                    "writers": sorted(info["writers"]),
                    "external_writers": sorted(info["external_writers"]),
                })

        # Sort by external_writer_count descending (most dangerous first), then total writer_count
        shared_state.sort(key=lambda x: (x["external_writer_count"], x["writer_count"]), reverse=True)
        return shared_state

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
            anti_patterns = [h for h in hotspots if h["is_anti_pattern"]]
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
            threading_hazards = [h for h in hotspots if h["is_threading_hazard"]]
            if threading_hazards:
                f.write("## 🔴 Threading Hazard Warnings\n\n")
                f.write("> [!CAUTION]\n")
                f.write("> The following methods mix UI thread dispatch with background work or use `Application.DoEvents()`, creating re-entrancy and thread safety risks.\n\n")
                f.write("| # | Symbol (FQN) | UI Dispatch | Task Spawn | BgWorker | DoEvents | Lock |\n")
                f.write("|---|--------------|-------------|------------|----------|----------|------|\n")
                for i, h in enumerate(threading_hazards[:30]):
                    tf = h["thread_flags"]
                    link = f"[`{h['fqn']}`]({h['file_name']}#L{h['line_start']})" if h['file_name'] else f"`{h['fqn']}`"
                    f.write(f"| {i+1} | {link} | {'✅' if tf['has_ui_dispatch'] else '—'} | {'✅' if tf['has_task_spawn'] else '—'} | {'✅' if tf['has_background_worker'] else '—'} | {'⚠️' if tf['has_do_events'] else '—'} | {'🔒' if tf['has_lock'] else '—'} |\n")
                f.write("\n---\n\n")

            # --- Shared Mutable State Warnings ---
            if shared_state:
                external_spaghetti = [s for s in shared_state if s["spaghetti_type"] in ("external", "mixed")]
                internal_spaghetti = [s for s in shared_state if s["spaghetti_type"] == "internal"]

                if external_spaghetti:
                    f.write("## 🔴 Shared Mutable State (Cross-Class — External Spaghetti)\n\n")
                    f.write("> [!CAUTION]\n")
                    f.write("> The following fields are **written by methods in multiple different classes**. This is the most dangerous form of implicit coupling.\n\n")
                    f.write("| # | Field | External Writers | Internal Writers | Readers | Type |\n")
                    f.write("|---|-------|-----------------|------------------|---------|------|\n")
                    for i, s in enumerate(external_spaghetti[:30]):
                        f.write(f"| {i+1} | `{s['target_fqn']}` | {s['external_writer_count']} | {s['internal_writer_count']} | {s['reader_count']} | {s['spaghetti_type']} |\n")
                    f.write("\n")

                    # Show top external spaghetti details
                    for s in external_spaghetti[:5]:
                        f.write(f"<details><summary>📝 {s['target_fqn']}</summary>\n\n")
                        f.write("**External writers:**\n")
                        for w in s["external_writers"]:
                            f.write(f"- `{w}`\n")
                        f.write("\n</details>\n\n")

                    f.write("---\n\n")

                if internal_spaghetti:
                    f.write("## ⚠️ Shared Mutable State (Same-Class — Internal Spaghetti)\n\n")
                    f.write("> [!WARNING]\n")
                    f.write("> The following fields are written by multiple methods within the same class. High counts indicate complex internal state management.\n\n")
                    f.write("| # | Field | Writers | Readers |\n")
                    f.write("|---|-------|---------|---------|\n")
                    for i, s in enumerate(internal_spaghetti[:30]):
                        f.write(f"| {i+1} | `{s['target_fqn']}` | {s['writer_count']} | {s['reader_count']} |\n")
                    f.write("\n---\n\n")

            # --- General Hotspots ---
            f.write("## Top 50 Hotspots (General)\n\n")
            f.write("| Rank | Score | Kind | Symbol (FQN) | LOC | Fan-in | Fan-out |\n")
            f.write("|------|-------|------|--------------|-----|--------|----------|\n")
            
            for i, h in enumerate(hotspots[:50]): # Top 50
                m = h["metrics"]
                link = f"[`{h['fqn']}`]({h['file_name']}#L{h['line_start']})" if h['file_name'] else f"`{h['fqn']}`"
                f.write(f"| {i+1} | {h['score']:.4f} | {h['kind']} | {link} | {m['loc']} | {m['fan_in']} | {m['fan_out']} |\n")

        logger.info(f"Generated hotspot reports: {json_path}, {md_path}")
        if shared_state:
            external_count = len([s for s in shared_state if s["spaghetti_type"] in ("external", "mixed")])
            internal_count = len([s for s in shared_state if s["spaghetti_type"] == "internal"])
            logger.info(f"Shared mutable state: {external_count} external spaghetti, {internal_count} internal spaghetti")
