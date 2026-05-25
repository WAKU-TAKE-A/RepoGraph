using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal static class DelegateReferenceExtractor
    {
        public static void ExtractDelegateReferences(SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            var invocations = SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var calleeSymbol = DirectCallExtractor.ResolveInvokedMethodSymbol(semanticModel, invocation);
                if (calleeSymbol != null)
                {
                    RecordDelegateArguments(semanticModel, symbolData, result, calleeSymbol, calleeSymbol.Parameters, invocation.ArgumentList.Arguments);
                }
            }

            var creations = SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<ObjectCreationExpressionSyntax>();
            foreach (var creation in creations)
            {
                if (creation.ArgumentList == null)
                {
                    continue;
                }

                var constructorSymbol = DirectCallExtractor.ResolveConstructedMethodSymbol(semanticModel, creation);
                if (constructorSymbol != null)
                {
                    RecordDelegateArguments(semanticModel, symbolData, result, constructorSymbol, constructorSymbol.Parameters, creation.ArgumentList.Arguments);
                }
            }
        }



        private static void RecordDelegateArguments(
            SemanticModel semanticModel,
            SymbolData symbolData,
            ExtractionResult result,
            IMethodSymbol? calleeSymbol,
            ImmutableArray<IParameterSymbol> parameters,
            SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            for (var i = 0; i < arguments.Count && i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (!IsDelegateLike(parameter.Type))
                {
                    continue;
                }

                foreach (var targetMethod in DelegateTargetResolver.ResolveDelegateTargetMethods(semanticModel, arguments[i].Expression))
                {
                    var handlerFqn = targetMethod.OriginalDefinition.ToDisplayString();
                    result.MethodCalls.Add(new MethodCallData
                    {
                        CallerId = symbolData.Fqn,
                        CalleeId = handlerFqn,
                        CallCount = 1,
                        CallType = "delegate_reference",
                        RuleId = null,
                        RuleFamily = null,
                        RuleMode = null
                    });

                }
            }
        }

        private static bool IsDelegateLike(ITypeSymbol? type)
        {
            if (type == null)
            {
                return false;
            }

            return type.TypeKind == TypeKind.Delegate
                || type.Name.StartsWith("Action", StringComparison.Ordinal)
                || type.Name.StartsWith("Func", StringComparison.Ordinal)
                || type.ToDisplayString().Contains("EventHandler", StringComparison.Ordinal);
        }
    }
}
