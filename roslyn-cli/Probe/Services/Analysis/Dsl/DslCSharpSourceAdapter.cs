using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis.Dsl
{
    public static class DslCSharpSourceAdapter
    {
        private static readonly SymbolDisplayFormat FqFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType
        );

        public static IReadOnlyDictionary<string, object?> FromSymbol(ISymbol symbol)
        {
            var dict = new Dictionary<string, object?>();
            dict["source_type"] = "csharp_symbol";
            
            // Basic symbol info
            dict["fqn"] = symbol.ToDisplayString(FqFormat);
            dict["name"] = symbol.Name;
            dict["kind"] = symbol.Kind.ToString().ToLowerInvariant();
            dict["containing_type"] = symbol.ContainingType?.Name;
            dict["containing_namespace"] = symbol.ContainingNamespace?.ToDisplayString();
            dict["is_static"] = symbol.IsStatic;

            if (symbol is IMethodSymbol methodSymbol)
            {
                dict["parameter_count"] = methodSymbol.Parameters.Length;
                dict["return_type"] = methodSymbol.ReturnType.ToDisplayString(FqFormat);
            }

            return dict;
        }

        public static IReadOnlyDictionary<string, object?> FromInvocation(
            InvocationExpressionSyntax invocation, 
            SemanticModel semanticModel,
            string callerFqn)
        {
            var dict = new Dictionary<string, object?>();
            dict["source_type"] = "csharp_invocation";
            dict["fqn"] = callerFqn;
            dict["argument_count"] = invocation.ArgumentList.Arguments.Count;

            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                dict["method.name"] = methodSymbol.Name;
                dict["method.fqn"] = methodSymbol.ToDisplayString(FqFormat);
                dict["method.containing_type"] = methodSymbol.ContainingType?.Name;
                dict["method.containing_namespace"] = methodSymbol.ContainingNamespace?.ToDisplayString();
                dict["invocation.generic_arg_count"] = methodSymbol.TypeArguments.Length;
                
                if (methodSymbol.TypeArguments.Length > 0)
                {
                    dict["invocation.generic_arg[0]"] = methodSymbol.TypeArguments[0].Name;
                }
            }
            else
            {
                // Fallback for unresolved invocation
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    dict["method.name"] = memberAccess.Name.Identifier.Text;
                }
                else if (invocation.Expression is IdentifierNameSyntax identifier)
                {
                    dict["method.name"] = identifier.Identifier.Text;
                }
            }

            return dict;
        }
    }
}
