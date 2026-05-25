using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis.Dsl
{
    public static class DslInvocationRecordCollector
    {
        private static readonly SymbolDisplayFormat FqFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType
        );

        public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Collect(
            SemanticModel semanticModel,
            SyntaxNode root)
        {
            var records = new List<IReadOnlyDictionary<string, object?>>();

            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var enclosingSymbol = semanticModel.GetEnclosingSymbol(invocation.SpanStart);
                if (enclosingSymbol != null)
                {
                    var callerFqn = enclosingSymbol.ToDisplayString(FqFormat);
                    var record = DslCSharpSourceAdapter.FromInvocation(invocation, semanticModel, callerFqn);
                    records.Add(record);
                }
            }

            return records;
        }
    }
}
