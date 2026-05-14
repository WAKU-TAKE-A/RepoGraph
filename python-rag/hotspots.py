import os
import json
from typing import List, Dict, Any
from sqlalchemy.orm import Session
from models import Symbol
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
        for symbol in symbols:
            fan_in = self.graph.call_graph.in_degree(symbol.fqn) if symbol.fqn in self.graph.call_graph else 0
            fan_out = self.graph.call_graph.out_degree(symbol.fqn) if symbol.fqn in self.graph.call_graph else 0
            
            # TODO: dependency_graph extraction not yet implemented in Probe, default to 0 for now
            static_coupling = self.graph.dependency_graph.degree(symbol.fqn) if symbol.fqn in self.graph.dependency_graph else 0
            
            loc = symbol.loc or 0
            parameter_count = symbol.parameter_count or 0
            async_flag = 1 if symbol.is_async else 0
            
            raw_metrics.append({
                "fqn": symbol.fqn,
                "kind": symbol.kind,
                "metrics": {
                    "fan_in": fan_in,
                    "fan_out": fan_out,
                    "loc": loc,
                    "parameter_count": parameter_count,
                    "async_flag": async_flag,
                    "static_coupling": static_coupling
                }
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
            
            scored_results.append({
                "fqn": r["fqn"],
                "kind": r["kind"],
                "score": round(score, 4),
                "metrics": m
            })
            
        # 4. Sort by score descending
        scored_results.sort(key=lambda x: x["score"], reverse=True)
        return scored_results

    def generate_reports(self):
        hotspots = self.compute_hotspots()
        
        if not hotspots:
            logger.warning("No hotspots computed.")
            return

        json_path = os.path.join(self.output_dir, "hotspots.json")
        md_path = os.path.join(self.output_dir, "hotspots.md")
        
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump(hotspots, f, indent=2)
            
        with open(md_path, "w", encoding="utf-8") as f:
            f.write("# Repository Hotspots\n\n")
            f.write("| Rank | Score | Kind | Symbol (FQN) | LOC | Fan-in | Fan-out |\n")
            f.write("|------|-------|------|--------------|-----|--------|---------|\n")
            
            for i, h in enumerate(hotspots[:50]): # Top 50
                m = h["metrics"]
                f.write(f"| {i+1} | {h['score']:.4f} | {h['kind']} | `{h['fqn']}` | {m['loc']} | {m['fan_in']} | {m['fan_out']} |\n")

        logger.info(f"Generated hotspot reports: {json_path}, {md_path}")
