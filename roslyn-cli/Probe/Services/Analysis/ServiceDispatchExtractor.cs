using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal record ServiceResolutionDetection(
        string TargetConstructorFqn,
        string CallType,
        string RuleId,
        string RuleFamily,
        string RuleMode
    );

    internal record MvvmToolkitMessageDispatchDetection(
        string TargetReceiveMethodFqn,
        string CallType,
        string RuleId,
        string RuleFamily,
        string RuleMode
    );

    internal record AutofacModuleTypeDetection(
        string TargetModuleFqn,
        string Kind,
        string RuleId,
        string RuleFamily,
        string RuleMode
    );

    internal record AutofacModuleLoadDetection(
        string TargetLoadMethodFqn,
        string CallType,
        string RuleId,
        string RuleFamily,
        string RuleMode
    );

    internal record AutofacModuleDispatchDetection(
        IReadOnlyList<AutofacModuleTypeDetection> TypeDependencies,
        IReadOnlyList<AutofacModuleLoadDetection> MethodCalls
    );

    internal static class ServiceDispatchExtractor
    {
        public static IEnumerable<ServiceResolutionDetection> TryExtractServiceResolutionDispatch(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            IMethodSymbol? calledMethod)
        {
            if (!IsServiceResolutionMethod(calledMethod, invocation, out var resolutionKind))
            {
                yield break;
            }

            var requestedTypeFqn = ResolveRequestedServiceType(semanticModel, invocation, calledMethod);
            if (string.IsNullOrWhiteSpace(requestedTypeFqn))
            {
                yield break;
            }

            var constructorTargets = compilationCache.ResolveServiceDispatchConstructors(requestedTypeFqn);
            foreach (var target in constructorTargets)
            {
                yield return new ServiceResolutionDetection(
                    target,
                    resolutionKind,
                    resolutionKind == FrameworkRuleCatalog.ServiceProviderDispatch.CallType ? FrameworkRuleCatalog.ServiceProviderDispatch.RuleId : FrameworkRuleCatalog.AutofacResolveDispatch.RuleId,
                    resolutionKind == FrameworkRuleCatalog.ServiceProviderDispatch.CallType ? FrameworkRuleCatalog.ServiceProviderDispatch.Family : FrameworkRuleCatalog.AutofacResolveDispatch.Family,
                    resolutionKind == FrameworkRuleCatalog.ServiceProviderDispatch.CallType ? FrameworkRuleCatalog.ServiceProviderDispatch.ModeName : FrameworkRuleCatalog.AutofacResolveDispatch.ModeName
                );
            }
        }

        private static bool IsServiceResolutionMethod(IMethodSymbol? calledMethod, InvocationExpressionSyntax invocation, out string resolutionKind)
        {
            resolutionKind = string.Empty;
            var methodName = calledMethod?.Name;
            var containingType = calledMethod?.ContainingType?.ToDisplayString() ?? string.Empty;

            if ((string.Equals(methodName, "GetRequiredService", StringComparison.Ordinal) ||
                 string.Equals(methodName, "GetService", StringComparison.Ordinal)) &&
                (containingType.StartsWith("Microsoft.Extensions.DependencyInjection.", StringComparison.Ordinal) ||
                 string.Equals(containingType, "System.IServiceProvider", StringComparison.Ordinal)))
            {
                resolutionKind = FrameworkRuleCatalog.ServiceProviderDispatch.CallType;
                return true;
            }

            if (string.Equals(methodName, "Resolve", StringComparison.Ordinal) &&
                containingType.StartsWith("Autofac.", StringComparison.Ordinal))
            {
                resolutionKind = FrameworkRuleCatalog.AutofacResolveDispatch.CallType;
                return true;
            }

            if (calledMethod == null && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var syntaxName = memberAccess.Name.Identifier.ValueText;
                if (syntaxName is "GetRequiredService" or "GetService")
                {
                    resolutionKind = FrameworkRuleCatalog.ServiceProviderDispatch.CallType;
                    return true;
                }

                if (string.Equals(syntaxName, "Resolve", StringComparison.Ordinal))
                {
                    resolutionKind = FrameworkRuleCatalog.AutofacResolveDispatch.CallType;
                    return true;
                }
            }

            return false;
        }

        public static IEnumerable<MvvmToolkitMessageDispatchDetection> TryExtractMvvmToolkitMessagingDispatch(
            CompilationAnalysisCache compilationCache,
            IMethodSymbol calledMethod,
            SymbolData symbolData)
        {
            if (!IsMvvmToolkitRegisterAll(calledMethod))
            {
                yield break;
            }

            foreach (var receiveMethod in compilationCache.GetMethodCandidates(symbolData.ContainingType ?? "", "Receive", 1))
            {
                yield return new MvvmToolkitMessageDispatchDetection(
                    receiveMethod,
                    FrameworkRuleCatalog.MvvmToolkitMessageDispatch.CallType,
                    FrameworkRuleCatalog.MvvmToolkitMessageDispatch.RuleId,
                    FrameworkRuleCatalog.MvvmToolkitMessageDispatch.Family,
                    FrameworkRuleCatalog.MvvmToolkitMessageDispatch.ModeName
                );
            }
        }

        private static bool IsMvvmToolkitRegisterAll(IMethodSymbol calledMethod)
        {
            var containingTypeName = calledMethod.ContainingType?.Name ?? "";
            var containingNamespace = calledMethod.ContainingNamespace?.ToDisplayString() ?? "";
            return string.Equals(calledMethod.Name, "RegisterAll", StringComparison.Ordinal)
                && containingNamespace.Contains("Toolkit.Mvvm.Messaging", StringComparison.Ordinal)
                && (containingTypeName.Contains("Messenger", StringComparison.Ordinal)
                    || containingTypeName.Contains("Extensions", StringComparison.Ordinal));
        }

        public static AutofacModuleDispatchDetection? TryExtractAutofacModuleDispatch(
            CompilationAnalysisCache compilationCache,
            IMethodSymbol calledMethod)
        {
            if (!IsAutofacRegisterAssemblyModules(calledMethod))
            {
                return null;
            }

            var typeDependencies = new List<AutofacModuleTypeDetection>();
            foreach (var moduleType in compilationCache.GetAutofacModuleTypes())
            {
                typeDependencies.Add(new AutofacModuleTypeDetection(
                    moduleType,
                    FrameworkRuleCatalog.AutofacReflectionRegistration.CallType,
                    FrameworkRuleCatalog.AutofacReflectionRegistration.RuleId,
                    FrameworkRuleCatalog.AutofacReflectionRegistration.Family,
                    FrameworkRuleCatalog.AutofacReflectionRegistration.ModeName
                ));
            }

            var methodCalls = new List<AutofacModuleLoadDetection>();
            foreach (var loadMethod in compilationCache.GetAutofacModuleLoadMethods())
            {
                methodCalls.Add(new AutofacModuleLoadDetection(
                    loadMethod,
                    FrameworkRuleCatalog.AutofacModuleLoad.CallType,
                    FrameworkRuleCatalog.AutofacModuleLoad.RuleId,
                    FrameworkRuleCatalog.AutofacModuleLoad.Family,
                    FrameworkRuleCatalog.AutofacModuleLoad.ModeName
                ));
            }

            return new AutofacModuleDispatchDetection(typeDependencies, methodCalls);
        }

        private static bool IsAutofacRegisterAssemblyModules(IMethodSymbol calledMethod)
        {
            var containingNamespace = calledMethod.ContainingNamespace?.ToDisplayString() ?? "";
            return string.Equals(calledMethod.Name, "RegisterAssemblyModules", StringComparison.Ordinal)
                && containingNamespace.Contains("Autofac", StringComparison.Ordinal);
        }

        internal static string? ResolveRequestedServiceType(
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            IMethodSymbol? calledMethod)
        {
            if (calledMethod is { IsGenericMethod: true } && calledMethod.TypeArguments.Length == 1)
            {
                return calledMethod.TypeArguments[0].OriginalDefinition.ToDisplayString();
            }

            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name is GenericNameSyntax genericName &&
                genericName.TypeArgumentList.Arguments.Count == 1)
            {
                var requestedType = semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
                if (requestedType != null)
                {
                    return requestedType.OriginalDefinition.ToDisplayString();
                }
            }

            if (invocation.Expression is GenericNameSyntax standaloneGenericName &&
                standaloneGenericName.TypeArgumentList.Arguments.Count == 1)
            {
                var requestedType = semanticModel.GetTypeInfo(standaloneGenericName.TypeArgumentList.Arguments[0]).Type;
                if (requestedType != null)
                {
                    return requestedType.OriginalDefinition.ToDisplayString();
                }
            }

            if (invocation.ArgumentList.Arguments.Count == 0)
            {
                return null;
            }

            var firstArgument = ReflectionDispatchExtractor.UnwrapExpression(invocation.ArgumentList.Arguments[0].Expression);
            if (firstArgument is TypeOfExpressionSyntax typeOfExpression)
            {
                var requestedType = semanticModel.GetTypeInfo(typeOfExpression.Type).Type;
                return requestedType?.OriginalDefinition.ToDisplayString();
            }

            return null;
        }
    }
}
