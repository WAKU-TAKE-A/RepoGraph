using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal static class DirectCallExtractor
    {
        /// <summary>
        /// Extract method invocation calls.
        /// </summary>
        public static void ExtractMethodCalls(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var invocations = SymbolExtractor.GetAnalysisDescendantNodes(methodNode).OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var calledMethod = ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
                if (calledMethod != null)
                {
                    result.MethodCalls.Add(new MethodCallData
                    {
                        CallerId = symbolData.Fqn,
                        CalleeId = calledMethod.OriginalDefinition.ToDisplayString(),
                        CallCount = 1
                    });

                    if (ShouldExpandDynamicDispatch(invocation, calledMethod))
                    {
                        foreach (var overrideFqn in compilationCache.GetDispatchTargets(calledMethod))
                        {
                            if (overrideFqn == calledMethod.OriginalDefinition.ToDisplayString())
                            {
                                continue;
                            }

                            result.MethodCalls.Add(new MethodCallData
                            {
                                CallerId = symbolData.Fqn,
                                CalleeId = overrideFqn,
                                CallCount = 1,
                                CallType = StructuralEdgeCatalog.DynamicDispatch
                            });
                        }
                    }
                }
                else
                {
                    foreach (var fallbackTarget in ResolveFallbackMethodTargets(compilationCache, semanticModel, invocation, symbolData))
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = fallbackTarget,
                            CallCount = 1,
                            CallType = StructuralEdgeCatalog.CallsFallback
                        });
                    }
                }
            }
        }

        private static bool ShouldExpandDynamicDispatch(InvocationExpressionSyntax invocation, IMethodSymbol calledMethod)
        {
            return calledMethod.IsAbstract || calledMethod.ContainingType?.TypeKind == TypeKind.Interface;
        }

        internal static IEnumerable<string> ResolveFallbackMethodTargets(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, InvocationExpressionSyntax invocation, SymbolData symbolData)
        {
            if (string.IsNullOrWhiteSpace(symbolData.ContainingType))
            {
                yield break;
            }

            string? methodName = null;
            if (invocation.Expression is IdentifierNameSyntax identifier)
            {
                methodName = identifier.Identifier.ValueText;
            }
            else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                     memberAccess.Expression is ThisExpressionSyntax)
            {
                methodName = memberAccess.Name.Identifier.ValueText;
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                yield break;
            }

            var argumentCount = invocation.ArgumentList.Arguments.Count;
            foreach (var candidate in compilationCache.GetMethodCandidates(symbolData.ContainingType!, methodName, argumentCount))
            {
                yield return candidate;
            }
        }

        internal static IMethodSymbol? ResolveCalledMethodSymbol(SymbolInfo symbolInfo)
        {
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                return methodSymbol;
            }

            if (symbolInfo.CandidateSymbols.Length == 1 && symbolInfo.CandidateSymbols[0] is IMethodSymbol candidateMethod)
            {
                return candidateMethod;
            }

            return null;
        }

        internal static IMethodSymbol? ResolveInvokedMethodSymbol(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
        {
            if (semanticModel.GetOperation(invocation) is Microsoft.CodeAnalysis.Operations.IInvocationOperation invocationOperation)
            {
                return invocationOperation.TargetMethod;
            }

            return ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
        }

        internal static IMethodSymbol? ResolveConstructedMethodSymbol(SemanticModel semanticModel, ObjectCreationExpressionSyntax creation)
        {
            if (semanticModel.GetOperation(creation) is Microsoft.CodeAnalysis.Operations.IObjectCreationOperation creationOperation)
            {
                return creationOperation.Constructor;
            }

            return ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(creation));
        }
    }
}
