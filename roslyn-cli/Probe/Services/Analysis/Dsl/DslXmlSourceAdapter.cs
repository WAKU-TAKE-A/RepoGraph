using System.Collections.Generic;
using System.Xml.Linq;

namespace Probe.Services.Analysis.Dsl
{
    public static class DslXmlSourceAdapter
    {
        public static IReadOnlyDictionary<string, object?> FromAttribute(
            XAttribute attribute,
            string documentPath,
            string? documentXClass)
        {
            var dict = new Dictionary<string, object?>();
            dict["source_type"] = "xml_attribute";
            dict["name"] = attribute.Name.LocalName;
            dict["value"] = attribute.Value;
            dict["document.path"] = documentPath;
            dict["document.x_class"] = documentXClass;
            dict["element.name"] = attribute.Parent?.Name.LocalName;

            var context = documentXClass ?? System.IO.Path.GetFileName(documentPath);
            dict["fqn"] = $"xaml::{context}::{attribute.Parent?.Name.LocalName}::{attribute.Name.LocalName}";

            return dict;
        }
    }
}
