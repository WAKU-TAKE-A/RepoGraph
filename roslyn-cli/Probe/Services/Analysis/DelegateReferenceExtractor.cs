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
                else
                {
                    TryRecordFrameworkDelegateArgumentsFromInvocationFallback(semanticModel, symbolData, result, invocation);
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
                else
                {
                    TryRecordFrameworkDelegateArgumentsFromObjectCreationFallback(semanticModel, symbolData, result, creation);
                }
            }
        }

        private static void TryRecordFrameworkDelegateArgumentsFromInvocationFallback(
            SemanticModel semanticModel,
            SymbolData symbolData,
            ExtractionResult result,
            InvocationExpressionSyntax invocation)
        {
            if (!TryDescribeFrameworkDelegateInvocationFallback(semanticModel, invocation, out var descriptor))
            {
                return;
            }

            RecordFrameworkDelegateFallbackTargets(semanticModel, symbolData, result, invocation.ArgumentList.Arguments, descriptor);
        }

        private static void TryRecordFrameworkDelegateArgumentsFromObjectCreationFallback(
            SemanticModel semanticModel,
            SymbolData symbolData,
            ExtractionResult result,
            ObjectCreationExpressionSyntax creation)
        {
            if (!TryDescribeFrameworkDelegateObjectCreationFallback(semanticModel, creation, out var descriptor))
            {
                return;
            }

            if (creation.ArgumentList != null)
            {
                RecordFrameworkDelegateFallbackTargets(semanticModel, symbolData, result, creation.ArgumentList.Arguments, descriptor);
            }
        }

        private static void RecordFrameworkDelegateFallbackTargets(
            SemanticModel semanticModel,
            SymbolData symbolData,
            ExtractionResult result,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            FrameworkDelegateFallbackDescriptor descriptor)
        {
            var hasDelegateTarget = false;
            foreach (var argument in arguments)
            {
                foreach (var targetMethod in DelegateTargetResolver.ResolveDelegateTargetMethods(semanticModel, argument.Expression))
                {
                    var handlerFqn = targetMethod.OriginalDefinition.ToDisplayString();
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

                    var dispatchCaller = SymbolExtractor.EnsureSyntheticFrameworkSymbol(
                        result,
                        descriptor.FrameworkCallerPrefix,
                        descriptor.FrameworkCallerName,
                        descriptor.FrameworkNamespace,
                        descriptor.FrameworkContainingType,
                        descriptor.ReturnType,
                        descriptor.ParameterCount);

                    result.MethodCalls.Add(new MethodCallData
                    {
                        CallerId = dispatchCaller,
                        CalleeId = handlerFqn,
                        CallCount = 1,
                        CallType = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.CallType,
                        RuleId = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.RuleId,
                        RuleFamily = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.Family,
                        RuleMode = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.ModeName
                    });

                    hasDelegateTarget = true;
                }
            }

            if (hasDelegateTarget)
            {
                var fallbackCaller = SymbolExtractor.EnsureSyntheticFrameworkSymbol(
                    result,
                    descriptor.FrameworkCallerPrefix,
                    descriptor.FrameworkCallerName,
                    descriptor.FrameworkNamespace,
                    descriptor.FrameworkContainingType,
                    descriptor.ReturnType,
                    descriptor.ParameterCount);

                result.MethodCalls.Add(new MethodCallData
                {
                    CallerId = symbolData.Fqn,
                    CalleeId = fallbackCaller,
                    CallCount = 1,
                    CallType = StructuralEdgeCatalog.CallsFallback
                });
            }
        }

        private static bool TryDescribeFrameworkDelegateInvocationFallback(
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            out FrameworkDelegateFallbackDescriptor descriptor)
        {
            descriptor = default;
            string? methodName = null;
            string containingType = "FrameworkCallback";
            string frameworkNamespace = "Framework.Delegates";
            string frameworkCallerPrefix = "framework::delegate_callback";
            string frameworkCallerName = "DelegateCallback";

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                methodName = memberAccess.Name.Identifier.ValueText;
                var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
                if (receiverType != null &&
                    SymbolExtractor.LooksLikeFrameworkOwnedSymbol(
                        receiverType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                        receiverType.ToDisplayString()))
                {
                    containingType = receiverType.ToDisplayString();
                    frameworkNamespace = "Framework.Delegates";
                    frameworkCallerPrefix = $"framework::{containingType}.{methodName}";
                    frameworkCallerName = methodName;
                    descriptor = new FrameworkDelegateFallbackDescriptor(
                        frameworkCallerPrefix,
                        frameworkCallerName,
                        frameworkNamespace,
                        containingType,
                        "void",
                        invocation.ArgumentList.Arguments.Count);
                    return true;
                }
            }
            else if (invocation.Expression is MemberBindingExpressionSyntax memberBinding)
            {
                methodName = memberBinding.Name.Identifier.ValueText;
                if (invocation.Parent is ConditionalAccessExpressionSyntax conditionalAccess)
                {
                    var receiverType = semanticModel.GetTypeInfo(conditionalAccess.Expression).Type;
                    if (receiverType != null &&
                        SymbolExtractor.LooksLikeFrameworkOwnedSymbol(
                            receiverType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                            receiverType.ToDisplayString()))
                    {
                        containingType = receiverType.ToDisplayString();
                        frameworkNamespace = "Framework.Delegates";
                        frameworkCallerPrefix = $"framework::{containingType}.{methodName}";
                        frameworkCallerName = methodName;
                        descriptor = new FrameworkDelegateFallbackDescriptor(
                            frameworkCallerPrefix,
                            frameworkCallerName,
                            frameworkNamespace,
                            containingType,
                            "void",
                            invocation.ArgumentList.Arguments.Count);
                        return true;
                    }
                }
            }
            else if (invocation.Expression is IdentifierNameSyntax identifierName)
            {
                methodName = identifierName.Identifier.ValueText;
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                return false;
            }

            if (string.Equals(methodName, "AddHook", StringComparison.Ordinal))
            {
                descriptor = new FrameworkDelegateFallbackDescriptor(
                    "framework::System.Windows.Interop.HwndSource.AddHook",
                    "AddHook",
                    "Framework.Delegates",
                    "System.Windows.Interop.HwndSource",
                    "IntPtr",
                    invocation.ArgumentList.Arguments.Count);
                return true;
            }

            return false;
        }

        private static bool TryDescribeFrameworkDelegateObjectCreationFallback(
            SemanticModel semanticModel,
            ObjectCreationExpressionSyntax creation,
            out FrameworkDelegateFallbackDescriptor descriptor)
        {
            descriptor = default;
            var createdType = semanticModel.GetTypeInfo(creation).Type
                ?? semanticModel.GetTypeInfo(creation.Type).Type;

            var createdTypeName = createdType?.ToDisplayString()
                ?? creation.Type.ToString();

            if (string.IsNullOrWhiteSpace(createdTypeName))
            {
                return false;
            }

            var simpleName = createdTypeName.Split('.').Last();
            if (simpleName.EndsWith("PropertyMetadata", StringComparison.Ordinal))
            {
                descriptor = new FrameworkDelegateFallbackDescriptor(
                    $"framework::{createdTypeName}.callback",
                    simpleName,
                    "Framework.UI",
                    createdTypeName,
                    "void",
                    creation.ArgumentList?.Arguments.Count ?? 0);
                return true;
            }

            if (createdType != null &&
                SymbolExtractor.LooksLikeFrameworkOwnedSymbol(
                    createdType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                    createdType.ToDisplayString()))
            {
                descriptor = new FrameworkDelegateFallbackDescriptor(
                    $"framework::{createdType.ToDisplayString()}.ctor",
                    simpleName,
                    "Framework.Delegates",
                    createdType.ToDisplayString(),
                    createdType.ToDisplayString(),
                    creation.ArgumentList?.Arguments.Count ?? 0);
                return true;
            }

            return false;
        }

        private readonly record struct FrameworkDelegateFallbackDescriptor(
            string FrameworkCallerPrefix,
            string FrameworkCallerName,
            string FrameworkNamespace,
            string FrameworkContainingType,
            string ReturnType,
            int ParameterCount);

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
                        CallType = FrameworkRuleCatalog.DelegateReference.CallType,
                        RuleId = FrameworkRuleCatalog.DelegateReference.RuleId,
                        RuleFamily = FrameworkRuleCatalog.DelegateReference.Family,
                        RuleMode = FrameworkRuleCatalog.DelegateReference.ModeName
                    });

                    if (calleeSymbol != null &&
                        TryGetFrameworkDelegateDispatchCallerFqn(result, calleeSymbol, parameter, out var dispatchCaller))
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = dispatchCaller,
                            CalleeId = handlerFqn,
                            CallCount = 1,
                            CallType = FrameworkRuleCatalog.FrameworkDelegateDispatch.CallType,
                            RuleId = FrameworkRuleCatalog.FrameworkDelegateDispatch.RuleId,
                            RuleFamily = FrameworkRuleCatalog.FrameworkDelegateDispatch.Family,
                            RuleMode = FrameworkRuleCatalog.FrameworkDelegateDispatch.ModeName
                        });
                    }
                }
            }
        }

        private static bool TryGetFrameworkDelegateDispatchCallerFqn(
            ExtractionResult result,
            IMethodSymbol calleeSymbol,
            IParameterSymbol delegateParameter,
            out string dispatchCaller)
        {
            dispatchCaller = string.Empty;
            var containingType = calleeSymbol.ContainingType?.ToDisplayString();
            var containingNamespace = calleeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (!SymbolExtractor.LooksLikeFrameworkOwnedSymbol(containingNamespace, containingType))
            {
                return false;
            }

            dispatchCaller = SymbolExtractor.EnsureSyntheticFrameworkSymbol(
                result,
                $"framework::{calleeSymbol.OriginalDefinition.ToDisplayString()}::{delegateParameter.Name}",
                delegateParameter.Name,
                "Framework.Delegates",
                containingType,
                delegateParameter.Type?.ToDisplayString(),
                delegateParameter.Type is INamedTypeSymbol namedType ? namedType.DelegateInvokeMethod?.Parameters.Length ?? 0 : 0);
            return !string.IsNullOrWhiteSpace(dispatchCaller);
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
