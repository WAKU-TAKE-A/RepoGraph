using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Probe.Services.Analysis.Dsl
{
    public static class DslXmlAttributeRecordCollector
    {
        public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Collect(IEnumerable<string> xmlPaths)
        {
            var records = new List<IReadOnlyDictionary<string, object?>>();

            foreach (var path in xmlPaths)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var doc = XDocument.Load(reader);
                    
                    if (doc.Root == null) continue;

                    var xClassAttr = doc.Root.Attributes().FirstOrDefault(a => a.Name.LocalName == "Class");
                    var documentXClass = xClassAttr?.Value;

                    foreach (var element in doc.Descendants())
                    {
                        foreach (var attribute in element.Attributes())
                        {
                            // Skip xmlns attributes
                            if (attribute.IsNamespaceDeclaration) continue;

                            var record = DslXmlSourceAdapter.FromAttribute(attribute, path, documentXClass);
                            records.Add(record);
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore XML parse or read errors
                }
            }

            return records;
        }
    }
}
