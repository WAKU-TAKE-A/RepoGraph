using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Probe.Services.Analysis
{
    public class SymbolExtractor
    {
        private readonly ILogger<SymbolExtractor> _logger;

        public SymbolExtractor(ILogger<SymbolExtractor> logger)
        {
            _logger = logger;
        }

        public IEnumerable<SymbolData> ExtractSymbols(Compilation compilation, SyntaxTree tree)
        {
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
                    yield return MapToData(symbol, node);
                }
            }
        }

        private SymbolData MapToData(ISymbol symbol, SyntaxNode node)
        {
            var data = new SymbolData
            {
                Id = Guid.NewGuid().ToString(), // Should be stable hash in real impl
                Fqn = symbol.ToDisplayString(),
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
                data.IsAsync = method.IsAsync;
                data.IsGeneric = method.IsGenericMethod;
                data.ParameterCount = method.Parameters.Length;
                data.ReturnType = method.ReturnType.ToDisplayString();
                data.IsExtensionMethod = method.IsExtensionMethod;
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
    }
}
