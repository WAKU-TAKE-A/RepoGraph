using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal static class EventSubscriptionExtractor
    {
        public static void ExtractEventSubscriptions(SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var assignments = SymbolExtractor.GetAnalysisDescendantNodes(methodNode).OfType<AssignmentExpressionSyntax>();
            foreach (var assignment in assignments)
            {
                if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
                    assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
                {
                    IEventSymbol? eventSymbol = null;
                    var leftInfo = semanticModel.GetSymbolInfo(assignment.Left);
                    if (leftInfo.Symbol is IEventSymbol resolvedEventSymbol)
                    {
                        eventSymbol = resolvedEventSymbol;
                        var eventFqn = eventSymbol.ToDisplayString();
                        var callType = assignment.IsKind(SyntaxKind.AddAssignmentExpression) ? "event_subscribe" : "event_unsubscribe";
                        
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = eventFqn,
                            CallCount = 1,
                            CallType = callType
                        });
                    }

                    var isSubscribe = assignment.IsKind(SyntaxKind.AddAssignmentExpression);
                    foreach (var handlerMethod in DelegateTargetResolver.ResolveDelegateTargetMethods(semanticModel, assignment.Right))
                    {
                        var handlerFqn = handlerMethod.OriginalDefinition.ToDisplayString();
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = handlerFqn,
                            CallCount = 1,
                            CallType = FrameworkRuleCatalog.DelegateReference.CallType,
                            RuleId = FrameworkRuleCatalog.DelegateReference.RuleId,
                            RuleFamily = FrameworkRuleCatalog.DelegateReference.Family,
                            RuleMode = FrameworkRuleCatalog.DelegateReference.ModeName
                        });

                        if (isSubscribe && eventSymbol != null)
                        {
                            var dispatchCaller = GetEventDispatchCallerFqn(semanticModel, eventSymbol, result);
                            if (!string.IsNullOrWhiteSpace(dispatchCaller))
                            {
                                result.MethodCalls.Add(new MethodCallData
                                {
                                    CallerId = dispatchCaller,
                                    CalleeId = handlerFqn,
                                    CallCount = 1,
                                    CallType = FrameworkRuleCatalog.EventDispatch.CallType,
                                    RuleId = FrameworkRuleCatalog.EventDispatch.RuleId,
                                    RuleFamily = FrameworkRuleCatalog.EventDispatch.Family,
                                    RuleMode = FrameworkRuleCatalog.EventDispatch.ModeName
                                });
                            }
                        }
                    }
                }
            }
        }

        private static string? GetEventDispatchCallerFqn(SemanticModel semanticModel, IEventSymbol eventSymbol, ExtractionResult result)
        {
            var eventFqn = eventSymbol.ToDisplayString();
            var eventNamespace = eventSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            var eventType = eventSymbol.ContainingType?.ToDisplayString();

            if (!SymbolExtractor.LooksLikeFrameworkOwnedSymbol(eventNamespace, eventType))
            {
                return eventFqn;
            }

            return SymbolExtractor.EnsureSyntheticFrameworkSymbol(
                result,
                $"framework::{eventFqn}",
                eventSymbol.Name,
                "Framework.Events",
                eventType,
                eventSymbol.Type?.ToDisplayString(),
                eventSymbol.Type is INamedTypeSymbol namedType ? namedType.DelegateInvokeMethod?.Parameters.Length ?? 0 : 0);
        }
    }
}
