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
                            CallType = "delegate_reference",
                            RuleId = null,
                            RuleFamily = null,
                            RuleMode = null
                        });

                    }
                }
            }
        }
    }
}
