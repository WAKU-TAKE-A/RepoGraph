using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Probe.Services.Analysis
{
    public class XamlRelationshipExtractor
    {
        private static readonly Regex StaticReferencePattern = new(@"^\{x:Static\s+(?<ref>[A-Za-z_][A-Za-z0-9_]*:[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\}$", RegexOptions.Compiled);
        private static readonly Regex TypeReferencePattern = new(@"^\{x:Type\s+(?<ref>[A-Za-z_][A-Za-z0-9_]*:[A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.Compiled);

        private readonly ILogger<XamlRelationshipExtractor> _logger;

        public XamlRelationshipExtractor(ILogger<XamlRelationshipExtractor> logger)
        {
            _logger = logger;
        }

        public XamlExtractionResult Extract(
            string projectName,
            string projectId,
            string projectPath,
            IEnumerable<string> xamlPaths,
            IReadOnlyDictionary<string, List<string>> methodsByTypeAndName,
            IReadOnlyDictionary<string, List<SymbolData>> membersByTypeAndName)
        {
            var result = new XamlExtractionResult();
            var fullProjectPath = Path.GetFullPath(projectPath);
            var projectDir = Path.GetDirectoryName(fullProjectPath) ?? fullProjectPath;
            var xamlClassByPath = BuildXamlClassMap(xamlPaths);

            foreach (var xamlPath in xamlPaths)
            {
                try
                {
                    var fullXamlPath = Path.GetFullPath(xamlPath);
                    if (!File.Exists(fullXamlPath))
                    {
                        continue;
                    }

                    var doc = XDocument.Load(fullXamlPath, LoadOptions.SetLineInfo);
                    var root = doc.Root;
                    if (root == null)
                    {
                        continue;
                    }

                    var xClass = GetXClass(root);
                    if (string.IsNullOrWhiteSpace(xClass))
                    {
                        continue;
                    }

                    var documentId = GetStableId(fullXamlPath);
                    result.Documents.Add(new XamlDocumentData
                    {
                        Id = documentId,
                        ProjectId = projectId,
                        FilePath = fullXamlPath,
                        Name = Path.GetFileName(fullXamlPath)
                    });

                    var symbolFqn = $"xaml::{xClass}::{Path.GetFileName(fullXamlPath)}";
                    result.Symbols.Add(CreateXamlSymbol(projectId, documentId, symbolFqn, xClass, fullXamlPath));
                    result.TypeDependencies.Add(new TypeDependencyData
                    {
                        SourceFqn = symbolFqn,
                        TargetFqn = xClass,
                        Kind = StructuralEdgeCatalog.XamlCodebehind
                    });

                    var clrNamespaceMappings = root.Attributes()
                        .Where(a => a.IsNamespaceDeclaration && (
                            a.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) ||
                            a.Value.StartsWith("using:", StringComparison.Ordinal)))
                        .Select(a => new
                        {
                            Prefix = a.Name.LocalName == "xmlns" ? string.Empty : a.Name.LocalName,
                            Value = a.Value
                        })
                        .ToDictionary(
                            x => x.Prefix,
                            x => ParseClrNamespaceMapping(x.Value),
                            StringComparer.Ordinal);

                    foreach (var element in root.DescendantsAndSelf())
                    {
                        if (TryResolveElementType(element, clrNamespaceMappings, out var elementTypeFqn))
                        {
                            result.TypeDependencies.Add(new TypeDependencyData
                            {
                                SourceFqn = symbolFqn,
                                TargetFqn = elementTypeFqn,
                                Kind = StructuralEdgeCatalog.XamlTypeUsage
                            });
                        }

                        foreach (var attribute in element.Attributes())
                        {
                            if (attribute.IsNamespaceDeclaration)
                            {
                                continue;
                            }

                            var attrName = attribute.Name.LocalName;
                            var attrValue = attribute.Value?.Trim();
                            if (string.IsNullOrWhiteSpace(attrValue))
                            {
                                continue;
                            }

                            if (TryResolveAttributeTypeReference(attrName, attrValue, clrNamespaceMappings, out var attributeTypeFqn))
                            {
                                result.TypeDependencies.Add(new TypeDependencyData
                                {
                                    SourceFqn = symbolFqn,
                                    TargetFqn = attributeTypeFqn,
                                    Kind = StructuralEdgeCatalog.XamlTypeUsage
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze XAML file {File}", xamlPath);
                }
            }

            return result;
        }

        private static Dictionary<string, string> BuildXamlClassMap(IEnumerable<string> xamlPaths)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var xamlPath in xamlPaths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(xamlPath);
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var doc = XDocument.Load(fullPath);
                    var root = doc.Root;
                    var xClass = root == null ? null : GetXClass(root);
                    if (!string.IsNullOrWhiteSpace(xClass))
                    {
                        result[fullPath] = xClass;
                    }
                }
                catch
                {
                    // Best-effort only. Individual file failures are handled in the main pass.
                }
            }

            return result;
        }

        private static bool TryResolveAttributeTypeReference(
            string attributeName,
            string attributeValue,
            IReadOnlyDictionary<string, ClrNamespaceMapping> mappings,
            out string typeFqn)
        {
            typeFqn = string.Empty;

            if (TryResolveStaticReference(attributeValue, mappings, out typeFqn))
            {
                return true;
            }

            if (TryResolveTypeReference(attributeValue, mappings, out typeFqn))
            {
                return true;
            }

            if (attributeName.EndsWith("DataType", StringComparison.OrdinalIgnoreCase) &&
                TryResolvePrefixedTypeReference(attributeValue, mappings, out typeFqn))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveStaticReference(
            string value,
            IReadOnlyDictionary<string, ClrNamespaceMapping> mappings,
            out string typeFqn)
        {
            typeFqn = string.Empty;
            var match = StaticReferencePattern.Match(value);
            if (!match.Success)
            {
                return false;
            }

            var rawReference = match.Groups["ref"].Value;
            var memberSeparator = rawReference.LastIndexOf('.');
            var typeReference = memberSeparator > 0 ? rawReference[..memberSeparator] : rawReference;
            return TryResolvePrefixedTypeReference(typeReference, mappings, out typeFqn);
        }

        private static bool TryResolveTypeReference(
            string value,
            IReadOnlyDictionary<string, ClrNamespaceMapping> mappings,
            out string typeFqn)
        {
            typeFqn = string.Empty;
            var match = TypeReferencePattern.Match(value);
            if (!match.Success)
            {
                return false;
            }

            return TryResolvePrefixedTypeReference(match.Groups["ref"].Value, mappings, out typeFqn);
        }

        private static bool TryResolvePrefixedTypeReference(
            string value,
            IReadOnlyDictionary<string, ClrNamespaceMapping> mappings,
            out string typeFqn)
        {
            typeFqn = string.Empty;
            var separatorIndex = value.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
            {
                return false;
            }

            var prefix = value[..separatorIndex];
            var localName = value[(separatorIndex + 1)..];
            if (string.IsNullOrWhiteSpace(localName) || localName.Contains('.'))
            {
                return false;
            }

            if (!mappings.TryGetValue(prefix, out var mapping) || string.IsNullOrWhiteSpace(mapping.Namespace))
            {
                return false;
            }

            typeFqn = $"{mapping.Namespace}.{localName}";
            return true;
        }

        private static bool TryResolveElementType(XElement element, IReadOnlyDictionary<string, ClrNamespaceMapping> mappings, out string typeFqn)
        {
            typeFqn = string.Empty;
            var prefix = element.GetPrefixOfNamespace(element.Name.Namespace) ?? string.Empty;
            if (!mappings.TryGetValue(prefix, out var mapping) || string.IsNullOrWhiteSpace(mapping.Namespace))
            {
                return false;
            }

            var localName = element.Name.LocalName;
            if (localName.Contains('.'))
            {
                return false;
            }

            typeFqn = $"{mapping.Namespace}.{localName}";
            return true;
        }

        private static string? GetXClass(XElement root)
        {
            return root.Attributes().FirstOrDefault(a =>
                    a.Name.LocalName == "Class" &&
                    a.Name.NamespaceName == "http://schemas.microsoft.com/winfx/2006/xaml")
                ?.Value;
        }

        private static SymbolData CreateXamlSymbol(string projectId, string documentId, string symbolFqn, string xClass, string fullXamlPath)
        {
            var lineCount = 0;
            using (var reader = new StreamReader(fullXamlPath, Encoding.UTF8, true))
            {
                while (reader.ReadLine() != null)
                {
                    lineCount++;
                }
            }

            var lastDot = xClass.LastIndexOf('.');
            var ns = lastDot >= 0 ? xClass[..lastDot] : null;

            return new SymbolData
            {
                Id = GetStableId(symbolFqn),
                ProjectId = projectId,
                DocumentId = documentId,
                Fqn = symbolFqn,
                Name = Path.GetFileNameWithoutExtension(fullXamlPath),
                Kind = SyntheticSymbolKindCatalog.Xaml,
                Namespace = ns,
                ContainingType = xClass,
                Accessibility = "private",
                LineStart = 1,
                LineEnd = Math.Max(1, lineCount),
                Loc = Math.Max(1, lineCount)
            };
        }

        private static string BuildMethodIndexKey(string containingType, string methodName)
        {
            return $"{containingType}|{methodName}";
        }

        private static ClrNamespaceMapping ParseClrNamespaceMapping(string value)
        {
            var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mapping = new ClrNamespaceMapping();
            foreach (var part in parts)
            {
                if (part.StartsWith("clr-namespace:", StringComparison.Ordinal))
                {
                    mapping.Namespace = part["clr-namespace:".Length..];
                }
                else if (part.StartsWith("using:", StringComparison.Ordinal))
                {
                    mapping.Namespace = part["using:".Length..];
                }
                else if (part.StartsWith("assembly=", StringComparison.Ordinal))
                {
                    mapping.Assembly = part["assembly=".Length..];
                }
            }

            return mapping;
        }

        private static string GetStableId(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public class XamlExtractionResult
    {
        public List<XamlDocumentData> Documents { get; } = new();
        public List<SymbolData> Symbols { get; } = new();
        public List<MethodCallData> MethodCalls { get; } = new();
        public List<TypeDependencyData> TypeDependencies { get; } = new();
    }

    public class XamlDocumentData
    {
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Name { get; set; } = "";
    }

    internal sealed class ClrNamespaceMapping
    {
        public string Namespace { get; set; } = "";
        public string Assembly { get; set; } = "";
    }
}
