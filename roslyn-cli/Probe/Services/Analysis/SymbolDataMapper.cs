using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Probe.Services.Analysis
{
    internal static class SymbolDataMapper
    {
        public static string GetStableHash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static bool IsPartialDeclaration(SyntaxNode node)
        {
            return node switch
            {
                BaseTypeDeclarationSyntax typeDecl => typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
                MethodDeclarationSyntax methodDecl => methodDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
                _ => false
            };
        }

        public static SymbolData MapToData(ISymbol symbol, SyntaxNode node)
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
                IsAsync = false,
                IsPartial = IsPartialDeclaration(node),
                IsGeneric = false,
                IsVolatile = false,
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
                if (method.MethodKind == MethodKind.Constructor)
                    data.Kind = "constructor";
                
                // Detect if method takes a callback/delegate to help identify "Callback Hell"
                data.HasCallback = method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate || p.Type.Name.StartsWith("Action") || p.Type.Name.StartsWith("Func"));
            }
            else if (symbol is INamedTypeSymbol type)
            {
                data.IsGeneric = type.IsGenericType;
                data.Kind = type.TypeKind.ToString().ToLower();
                data.IsDisposable = type.AllInterfaces.Any(i => i.ToDisplayString() == "System.IDisposable");
            }
            else if (symbol is IFieldSymbol fieldSymbol)
            {
                data.IsVolatile = fieldSymbol.IsVolatile;
            }

            return data;
        }

        public static SymbolData CreateAnonymousFunctionData(IMethodSymbol enclosingSymbol, AnonymousFunctionExpressionSyntax anonymousFunction)
        {
            var lineSpan = anonymousFunction.GetLocation().GetLineSpan();
            var line = lineSpan.StartLinePosition.Line + 1;
            var column = lineSpan.StartLinePosition.Character + 1;
            var name = $"<lambda@L{line}C{column}>";
            var fqn = $"{enclosingSymbol.OriginalDefinition.ToDisplayString()}.{name}";

            var parameterCount = anonymousFunction switch
            {
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
                SimpleLambdaExpressionSyntax => 1,
                AnonymousMethodExpressionSyntax anonymousMethod when anonymousMethod.ParameterList != null => anonymousMethod.ParameterList.Parameters.Count,
                _ => 0
            };

            return new SymbolData
            {
                Id = GetStableHash(fqn),
                Fqn = fqn,
                Name = name,
                Kind = SyntheticSymbolKindCatalog.Lambda,
                Namespace = enclosingSymbol.ContainingNamespace?.ToDisplayString(),
                ContainingType = enclosingSymbol.ContainingType?.ToDisplayString(),
                Accessibility = "private",
                IsStatic = enclosingSymbol.IsStatic,
                IsAsync = anonymousFunction.AsyncKeyword != default,
                LineStart = line,
                LineEnd = lineSpan.EndLinePosition.Line + 1,
                Loc = anonymousFunction.ToString().Split('\n').Length,
                ParameterCount = parameterCount,
                ReturnType = "unknown"
            };
        }

        public static SymbolData CreateFrameworkMethodSymbol(IMethodSymbol method)
        {
            var fqn = method.OriginalDefinition.ToDisplayString();
            return new SymbolData
            {
                Id = GetStableHash(fqn),
                Fqn = fqn,
                Name = method.Name,
                Kind = SyntheticSymbolKindCatalog.FrameworkMethod,
                Namespace = method.ContainingNamespace?.ToDisplayString(),
                ContainingType = method.ContainingType?.ToDisplayString(),
                Accessibility = method.DeclaredAccessibility.ToString().ToLower(),
                IsStatic = method.IsStatic,
                IsAbstract = method.IsAbstract,
                IsSealed = method.IsSealed,
                IsAsync = method.IsAsync || method.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task"),
                IsPartial = false,
                IsGeneric = method.IsGenericMethod,
                IsExtensionMethod = method.IsExtensionMethod,
                IsDisposable = false,
                IsVolatile = false,
                LineStart = 0,
                LineEnd = 0,
                Loc = 0,
                ParameterCount = method.Parameters.Length,
                ReturnType = method.ReturnType.ToDisplayString(),
                HasCallback = method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate || p.Type.Name.StartsWith("Action") || p.Type.Name.StartsWith("Func"))
            };
        }
    }
}
