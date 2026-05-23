import tempfile
import unittest
from pathlib import Path

from graph import GraphLoader
from models import Document, Symbol
from summarize import Summarizer


def write_graph(path: Path, payload: str) -> None:
    path.write_text(payload.strip(), encoding="utf-8")


class SummarizerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.workspace = Path(self.temp_dir.name)
        graphs_dir = self.workspace / "output" / "graphs"
        graphs_dir.mkdir(parents=True, exist_ok=True)

        write_graph(
            graphs_dir / "call_graph.json",
            """
{
  "directed": true,
  "multigraph": false,
  "graph": {},
  "nodes": [
    { "id": "Demo.Widget.Render()" },
    { "id": "Demo.Widget.Initialize()" },
    { "id": "Demo.Widget.Draw()" },
    { "id": "Demo.Host.Run()" }
  ],
  "links": [
    { "source": "Demo.Host.Run()", "target": "Demo.Widget.Render()", "type": "calls" },
    { "source": "Demo.Widget.Render()", "target": "Demo.Widget.Initialize()", "type": "calls" },
    { "source": "Demo.Widget.Render()", "target": "Demo.Widget.Draw()", "type": "calls" }
  ]
}
""",
        )
        write_graph(
            graphs_dir / "inheritance_graph.json",
            """
{
  "directed": true,
  "multigraph": false,
  "graph": {},
  "nodes": [
    { "id": "Demo.Widget.Render()" },
    { "id": "Demo.Controls.BaseWidget" },
    { "id": "Demo.Controls.DerivedWidget" }
  ],
  "links": [
    { "source": "Demo.Widget.Render()", "target": "Demo.Controls.BaseWidget", "type": "extends" },
    { "source": "Demo.Controls.DerivedWidget", "target": "Demo.Widget.Render()", "type": "extends" }
  ]
}
""",
        )
        for name in ("dependency_graph.json", "field_access_graph.json", "type_dependency_graph.json"):
            write_graph(
                graphs_dir / name,
                """
{
  "directed": true,
  "multigraph": false,
  "graph": {},
  "nodes": [],
  "links": []
}
""",
            )

        self.graph_loader = GraphLoader(str(self.workspace / "output" / "graphs"))
        self.graph_loader.load_all()
        self.summarizer = Summarizer(self.graph_loader)
        self.document = Document(file_path=r"C:\repo\Demo\Widget.cs", file_name="Widget.cs")

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def test_summarize_symbol_includes_metrics_and_relationships(self) -> None:
        symbol = Symbol(
            fqn="Demo.Widget.Render()",
            name="Render",
            kind="method",
            accessibility="public",
            is_async=1,
            is_static=1,
            is_abstract=0,
            loc=12,
            parameter_count=1,
            return_type="System.Void",
            document=self.document,
        )

        summary = self.summarizer.summarize_symbol(symbol)

        self.assertIn("method Demo.Widget.Render() [public]  [async static]", summary)
        self.assertIn("LOC:12 | params:1 | returns:System.Void", summary)
        self.assertIn("fan_in:1 | fan_out:2", summary)
        self.assertIn("called_by: Run()", summary)
        self.assertIn("calls: Initialize(), Draw()", summary)
        self.assertIn("inherits: BaseWidget", summary)
        self.assertIn("inherited_by: DerivedWidget", summary)

    def test_summarize_symbol_handles_missing_graph_membership(self) -> None:
        symbol = Symbol(
            fqn="Demo.Widget.Unknown()",
            name="Unknown",
            kind="method",
            accessibility=None,
            is_async=0,
            is_static=0,
            is_abstract=0,
            loc=3,
            parameter_count=0,
            return_type=None,
            document=self.document,
        )

        summary = self.summarizer.summarize_symbol(symbol)

        self.assertIn("method Demo.Widget.Unknown()", summary)
        self.assertIn("LOC:3 | params:0", summary)
        self.assertNotIn("fan_in:", summary)
        self.assertNotIn("inherits:", summary)


if __name__ == "__main__":
    unittest.main()
