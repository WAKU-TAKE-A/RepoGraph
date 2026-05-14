from models import Symbol, Project
from graph import GraphLoader

class Summarizer:
    def __init__(self, graph_loader: GraphLoader):
        self.graph = graph_loader

    def summarize_symbol(self, symbol: Symbol) -> str:
        summary_parts = []
        
        # Header
        accessibility = f"[{symbol.accessibility}] " if symbol.accessibility else ""
        flags = []
        if symbol.is_async: flags.append("async")
        if symbol.is_static: flags.append("static")
        if symbol.is_abstract: flags.append("abstract")
        flag_str = f" [{' '.join(flags)}]" if flags else ""
        
        header = f"{symbol.kind} {symbol.fqn} {accessibility}{flag_str}".strip()
        summary_parts.append(header)
        
        # Metrics
        metrics = []
        if symbol.loc is not None: metrics.append(f"LOC:{symbol.loc}")
        if symbol.parameter_count is not None: metrics.append(f"params:{symbol.parameter_count}")
        if symbol.return_type: metrics.append(f"returns:{symbol.return_type}")
        if metrics:
            summary_parts.append(" | ".join(metrics))
            
        # Graph relationships
        if symbol.fqn in self.graph.call_graph:
            fan_in = self.graph.call_graph.in_degree(symbol.fqn)
            fan_out = self.graph.call_graph.out_degree(symbol.fqn)
            summary_parts.append(f"fan_in:{fan_in} | fan_out:{fan_out}")
            
            # Optionally add top callers/callees if needed
            callers = list(self.graph.call_graph.predecessors(symbol.fqn))[:3]
            if callers:
                summary_parts.append(f"called_by: {', '.join(c.split('.')[-1] for c in callers)}")
                
            callees = list(self.graph.call_graph.successors(symbol.fqn))[:3]
            if callees:
                summary_parts.append(f"calls: {', '.join(c.split('.')[-1] for c in callees)}")

        if symbol.fqn in self.graph.inheritance_graph:
            bases = list(self.graph.inheritance_graph.successors(symbol.fqn))
            if bases:
                summary_parts.append(f"inherits: {', '.join(b.split('.')[-1] for b in bases)}")
            derived = list(self.graph.inheritance_graph.predecessors(symbol.fqn))
            if derived:
                summary_parts.append(f"inherited_by: {', '.join(d.split('.')[-1] for d in derived[:3])}")
                
        return "\n".join(summary_parts)
