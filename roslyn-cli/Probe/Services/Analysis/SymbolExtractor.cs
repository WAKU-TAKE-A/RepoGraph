using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Probe.Services.Analysis
{
    public class SymbolExtractor
    {
        private readonly ILogger<SymbolExtractor> _logger;

        public SymbolExtractor(ILogger<SymbolExtractor> logger)
        {
            _logger = logger;
        }

        public ExtractionResult Extract(Compilation compilation, SyntaxTree tree)
        {
            var result = new ExtractionResult();
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                ISymbol symbol = null;
                
                if (node is BaseTypeDeclarationSyntax typeDecl)
                {
                    symbol = semanticModel.GetDeclaredSymbol(typeDecl);
                }
                else if (node is MethodDeclarationSyntax methodDecl)
                {
                    symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                }
                else if (node is PropertyDeclarationSyntax propDecl)
                {
                    symbol = semanticModel.GetDeclaredSymbol(propDecl);
                }
                else if (node is FieldDeclarationSyntax fieldDecl)
                {
                    // Fields can have multiple variables, handle first for simplicity or all
                    var variable = fieldDecl.Declaration.Variables.FirstOrDefault();
                    if (variable != null)
                        symbol = semanticModel.GetDeclaredSymbol(variable);
                }

                if (symbol != null)
                {
                    var symbolData = MapToData(symbol, node);
                    result.Symbols.Add(symbolData);

                    // Extract inheritance
                    if (symbol is INamedTypeSymbol namedType)
                    {
                        if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
                        {
                            result.Inheritances.Add(new InheritanceData
                            {
                                DerivedId = symbolData.Fqn,
                                BaseId = namedType.BaseType.ToDisplayString(),
                                Kind = "extends"
                            });
                        }
                        foreach (var iface in namedType.Interfaces)
                        {
                            result.Inheritances.Add(new InheritanceData
                            {
                                DerivedId = symbolData.Fqn,
                                BaseId = iface.ToDisplayString(),
                                Kind = "implements"
                            });
                        }
                    }

                    // Extract method calls
                    if (node is MethodDeclarationSyntax methodNode && symbol is IMethodSymbol methodSymbol)
                    {
                        var invocations = methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>();
                        foreach (var invocation in invocations)
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                            {
                                result.MethodCalls.Add(new MethodCallData
                                {
                                    CallerId = symbolData.Fqn,
                                    CalleeId = calledMethod.OriginalDefinition.ToDisplayString(),
                                    CallCount = 1
                                });
                            }
                        }
                    }
                }
            }

            // Aggregate call counts
            result.MethodCalls = result.MethodCalls
                .GroupBy(c => new { c.CallerId, c.CalleeId })
                .Select(g => new MethodCallData
                {
                    CallerId = g.Key.CallerId,
                    CalleeId = g.Key.CalleeId,
                    CallCount = g.Sum(x => x.CallCount)
                }).ToList();

            return result;
        }

        private static string GetStableHash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private SymbolData MapToData(ISymbol symbol, SyntaxNode node)
        {
            var fqn = symbol.ToDisplayString();
            var data = new SymbolData
            {
                Id = GetStableHash(fqn), // Use stable hash for ID
                Fqn = fqn,
                Name = symbol.Name,
                Kind = symbol.Kind.ToString().ToLower(),
                Namespace = symbol.ContainingNamespace?.ToDisplayString(),
                ContainingType = symbol.ContainingType?.ToDisplayString(),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                IsStatic = symbol.IsStatic,
                IsAbstract = symbol.IsAbstract,
                IsSealed = symbol.IsSealed,
                IsAsync = false, // Check below
                IsPartial = false, // Check below
                IsGeneric = false, // Check below
                LineStart = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                LineEnd = node.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                Loc = node.ToString().Split('\n').Length
            };

            if (symbol is IMethodSymbol method)
            {
                data.IsAsync = method.IsAsync || method.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task");
                data.IsGeneric = method.IsGenericMethod;
                data.ParameterCount = method.Parameters.Length;
                data.ReturnType = method.ReturnType.ToDisplayString();
                data.IsExtensionMethod = method.IsExtensionMethod;
                
                // Future-proofing: Detect if method takes a callback/delegate to help identify "Callback Hell"
                data.HasCallback = method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate || p.Type.Name.StartsWith("Action") || p.Type.Name.StartsWith("Func"));
            }
            else if (symbol is INamedTypeSymbol type)
            {
                data.IsGeneric = type.IsGenericType;
                data.Kind = type.TypeKind.ToString().ToLower();
                data.IsDisposable = type.AllInterfaces.Any(i => i.ToDisplayString() == "System.IDisposable");
            }

            return data;
        }
    }

    public class SymbolData
    {
        public string Id { get; set; }
        public string DocumentId { get; set; }
        public string ProjectId { get; set; }
        public string Fqn { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Namespace { get; set; }
        public string ContainingType { get; set; }
        public string Accessibility { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsAsync { get; set; }
        public bool IsPartial { get; set; }
        public bool IsGeneric { get; set; }
        public bool IsExtensionMethod { get; set; }
        public bool IsDisposable { get; set; }
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public int Loc { get; set; }
        public int ParameterCount { get; set; }
        public string ReturnType { get; set; }
        public bool HasCallback { get; set; }
    }

    public class MethodCallData
    {
        public string CallerId { get; set; }
        public string CalleeId { get; set; }
        public int CallCount { get; set; }
    }

    public class InheritanceData
    {
        public string DerivedId { get; set; }
        public string BaseId { get; set; }
        public string Kind { get; set; }
    }

    public class ExtractionResult
    {
        public List<SymbolData> Symbols { get; set; } = new List<SymbolData>();
        public List<MethodCallData> MethodCalls { get; set; } = new List<MethodCallData>();
        public List<InheritanceData> Inheritances { get; set; } = new List<InheritanceData>();
    }
}
