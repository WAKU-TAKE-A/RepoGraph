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
        private static readonly Regex HandlerNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex StaticReferencePattern = new(@"^\{x:Static\s+(?<ref>[A-Za-z_][A-Za-z0-9_]*:[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\}$", RegexOptions.Compiled);
        private static readonly Regex TypeReferencePattern = new(@"^\{x:Type\s+(?<ref>[A-Za-z_][A-Za-z0-9_]*:[A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.Compiled);
        private static readonly Regex XBindPattern = new(@"^\{x:Bind\s+(?<path>[^,}]+)", RegexOptions.Compiled);
        private static readonly Regex BindingPathPattern = new(@"^\{Binding\s+(?<path>[^,}]+)", RegexOptions.Compiled);
        private static readonly Regex BindingNamedPathPattern = new(@"Path\s*=\s*(?<path>[^,}]+)", RegexOptions.Compiled);
        private static readonly HashSet<string> KnownEventAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Startup", "Exit", "DispatcherUnhandledException", "Loaded", "Unloaded",
            "Initialized", "Click", "Checked", "Unchecked", "Indeterminate",
            "SelectionChanged", "TextChanged", "ValueChanged", "Executed", "CanExecute",
            "MouseDown", "MouseUp", "MouseMove", "MouseEnter", "MouseLeave",
            "PreviewMouseDown", "PreviewMouseUp", "PreviewMouseMove", "PreviewMouseWheel",
            "KeyDown", "KeyUp", "PreviewKeyDown", "PreviewKeyUp", "PreviewTextInput",
            "Drop", "DragEnter", "DragLeave", "DragOver", "Closing", "Closed",
            "Activated", "Deactivated", "ContentRendered", "SourceInitialized",
            "Tick", "ScrollChanged", "CellEditEnding", "RowEditEnding",
            "GotFocus", "LostFocus", "TargetUpdated", "SizeChanged"
        };

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
                        Kind = "xaml_codebehind"
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
                                Kind = "xaml_type_usage"
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

                            if (LooksLikeEventAttribute(attrName) && LooksLikeHandlerName(attrValue))
                            {
                                if (methodsByTypeAndName.TryGetValue(BuildMethodIndexKey(xClass, attrValue), out var handlers))
                                {
                                    foreach (var handlerFqn in handlers)
                                    {
                                        result.MethodCalls.Add(new MethodCallData
                                        {
                                            CallerId = symbolFqn,
                                            CalleeId = handlerFqn,
                                            CallCount = 1,
                                            CallType = "xaml_event"
                                        });
                                    }
                                }
                            }

                            if (LooksLikeActionMethodAttribute(attrName) && LooksLikeHandlerName(attrValue))
                            {
                                if (methodsByTypeAndName.TryGetValue(BuildMethodIndexKey(xClass, attrValue), out var actionHandlers))
                                {
                                    foreach (var handlerFqn in actionHandlers)
                                    {
                                        result.MethodCalls.Add(new MethodCallData
                                        {
                                            CallerId = symbolFqn,
                                            CalleeId = handlerFqn,
                                            CallCount = 1,
                                            CallType = "xaml_action_binding"
                                        });
                                    }
                                }
                            }

                            if (attrName.EndsWith("Command", StringComparison.OrdinalIgnoreCase) &&
                                TryResolveBoundSymbolFqns(element, attrValue, xClass, clrNamespaceMappings, membersByTypeAndName, out var boundCommandSymbols))
                            {
                                foreach (var commandSymbol in boundCommandSymbols)
                                {
                                    result.TypeDependencies.Add(new TypeDependencyData
                                    {
                                        SourceFqn = symbolFqn,
                                        TargetFqn = commandSymbol,
                                        Kind = "xaml_command_binding"
                                    });
                                }
                            }

                            if (TryResolveAttributeTypeReference(attrName, attrValue, clrNamespaceMappings, out var attributeTypeFqn))
                            {
                                result.TypeDependencies.Add(new TypeDependencyData
                                {
                                    SourceFqn = symbolFqn,
                                    TargetFqn = attributeTypeFqn,
                                    Kind = "xaml_type_usage"
                                });
                            }

                            if (attrName.Equals("StartupUri", StringComparison.OrdinalIgnoreCase))
                            {
                                var targetClass = ResolveStartupUriTarget(fullXamlPath, attrValue, xamlClassByPath, projectDir);
                                if (!string.IsNullOrWhiteSpace(targetClass))
                                {
                                    result.TypeDependencies.Add(new TypeDependencyData
                                    {
                                        SourceFqn = symbolFqn,
                                        TargetFqn = targetClass,
                                        Kind = "xaml_navigation"
                                    });
                                }
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

        private static string? ResolveStartupUriTarget(string currentXamlPath, string startupUriValue, IReadOnlyDictionary<string, string> xamlClassByPath, string projectDir)
        {
            var cleanValue = startupUriValue.Split('#')[0].Trim();
            if (string.IsNullOrWhiteSpace(cleanValue))
            {
                return null;
            }

            var candidatePaths = new List<string>();
            if (Path.IsPathRooted(cleanValue))
            {
                candidatePaths.Add(Path.GetFullPath(cleanValue));
            }
            else
            {
                var currentDir = Path.GetDirectoryName(currentXamlPath) ?? projectDir;
                candidatePaths.Add(Path.GetFullPath(Path.Combine(currentDir, cleanValue)));
                candidatePaths.Add(Path.GetFullPath(Path.Combine(projectDir, cleanValue)));
            }

            foreach (var candidate in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (xamlClassByPath.TryGetValue(candidate, out var xClass))
                {
                    return xClass;
                }
            }

            return null;
        }

        private static bool LooksLikeEventAttribute(string name)
        {
            if (KnownEventAttributes.Contains(name))
            {
                return true;
            }

            return name.EndsWith("Changed", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Click", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Loaded", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Executed", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("CanExecute", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Closing", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Closed", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("SelectionChanged", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("MouseDown", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("MouseUp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("MouseMove", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("KeyDown", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("KeyUp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeActionMethodAttribute(string name)
        {
            return name.Equals("MethodName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Action", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Execute", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeHandlerName(string value)
        {
            return HandlerNamePattern.IsMatch(value);
        }

        private static bool TryResolveBoundSymbolFqns(
            XElement element,
            string attributeValue,
            string xClass,
            IReadOnlyDictionary<string, ClrNamespaceMapping> mappings,
            IReadOnlyDictionary<string, List<SymbolData>> membersByTypeAndName,
            out IReadOnlyCollection<string> symbolFqns)
        {
            symbolFqns = Array.Empty<string>();
            if (!TryExtractBindingPath(attributeValue, out var bindingPath))
            {
                return false;
            }

            var contextType = ResolveBindingContextType(element, xClass, mappings);
            if (string.IsNullOrWhiteSpace(contextType))
            {
                return false;
            }

            var resolved = ResolveBindingPathSymbols(contextType, bindingPath, membersByTypeAndName)
                .Select(symbol => symbol.Fqn)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (resolved.Count == 0)
            {
                return false;
            }

            symbolFqns = resolved;
            return true;
        }

        private static bool TryExtractBindingPath(string attributeValue, out string bindingPath)
        {
            bindingPath = string.Empty;
            if (string.IsNullOrWhiteSpace(attributeValue))
            {
                return false;
            }

            var xBindMatch = XBindPattern.Match(attributeValue);
            if (xBindMatch.Success)
            {
                bindingPath = xBindMatch.Groups["path"].Value.Trim();
                return !string.IsNullOrWhiteSpace(bindingPath);
            }

            var bindingMatch = BindingPathPattern.Match(attributeValue);
            if (bindingMatch.Success)
            {
                bindingPath = bindingMatch.Groups["path"].Value.Trim();
                if (bindingPath.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    bindingPath = bindingPath["Path=".Length..].Trim();
                }

                return !string.IsNullOrWhiteSpace(bindingPath);
            }

            var namedPathMatch = BindingNamedPathPattern.Match(attributeValue);
            if (namedPathMatch.Success)
            {
                bindingPath = namedPathMatch.Groups["path"].Value.Trim();
                return !string.IsNullOrWhiteSpace(bindingPath);
            }

            return false;
        }

        private static string ResolveBindingContextType(XElement element, string xClass, IReadOnlyDictionary<string, ClrNamespaceMapping> mappings)
        {
            foreach (var current in element.AncestorsAndSelf())
            {
                var dataTypeAttribute = current.Attributes()
                    .FirstOrDefault(attribute =>
                        attribute.Name.LocalName == "DataType" &&
                        attribute.Name.NamespaceName == "http://schemas.microsoft.com/winfx/2006/xaml");
                if (dataTypeAttribute != null &&
                    TryResolvePrefixedTypeReference(dataTypeAttribute.Value, mappings, out var dataType))
                {
                    return dataType;
                }
            }

            return xClass;
        }

        private static IEnumerable<SymbolData> ResolveBindingPathSymbols(
            string rootType,
            string bindingPath,
            IReadOnlyDictionary<string, List<SymbolData>> membersByTypeAndName)
        {
            var segments = bindingPath
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                yield break;
            }

            foreach (var symbol in ResolveBindingPathSymbolsRecursive(rootType, segments, 0, membersByTypeAndName, new HashSet<string>(StringComparer.Ordinal)))
            {
                yield return symbol;
            }
        }

        private static IEnumerable<SymbolData> ResolveBindingPathSymbolsRecursive(
            string currentType,
            string[] segments,
            int index,
            IReadOnlyDictionary<string, List<SymbolData>> membersByTypeAndName,
            HashSet<string> visited)
        {
            var key = $"{currentType}|{segments[index]}";
            if (!visited.Add($"{key}|{index}"))
            {
                yield break;
            }

            if (!membersByTypeAndName.TryGetValue(key, out var members))
            {
                yield break;
            }

            foreach (var member in members)
            {
                if (index == segments.Length - 1)
                {
                    yield return member;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(member.ReturnType))
                {
                    continue;
                }

                foreach (var nested in ResolveBindingPathSymbolsRecursive(member.ReturnType!, segments, index + 1, membersByTypeAndName, visited))
                {
                    yield return nested;
                }
            }
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
                Kind = "xaml",
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
