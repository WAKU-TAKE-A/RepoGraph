using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal static class ServiceRegistrationCollector
    {
        public static void CollectServiceRegistrations(Compilation compilation, Dictionary<string, HashSet<string>> serviceRegistrations)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (TryExtractMicrosoftDiRegistration(semanticModel, invocation, out var serviceType, out var implementationType) &&
                        !string.IsNullOrWhiteSpace(serviceType) &&
                        !string.IsNullOrWhiteSpace(implementationType))
                    {
                        AddDispatchTarget(serviceRegistrations, serviceType, implementationType);
                    }

                    if (TryExtractAutofacRegistration(semanticModel, invocation, out serviceType, out implementationType) &&
                        !string.IsNullOrWhiteSpace(serviceType) &&
                        !string.IsNullOrWhiteSpace(implementationType))
                    {
                        AddDispatchTarget(serviceRegistrations, serviceType, implementationType);
                    }
                }
            }
        }

        private static bool TryExtractMicrosoftDiRegistration(
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            out string serviceType,
            out string implementationType)
        {
            serviceType = string.Empty;
            implementationType = string.Empty;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return false;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (methodName is not "AddSingleton" and not "AddScoped" and not "AddTransient")
            {
                return false;
            }

            if (memberAccess.Name is GenericNameSyntax genericName)
            {
                if (genericName.TypeArgumentList.Arguments.Count == 2)
                {
                    serviceType = ResolveTypeArgumentFqn(semanticModel, genericName.TypeArgumentList.Arguments[0]);
                    implementationType = ResolveTypeArgumentFqn(semanticModel, genericName.TypeArgumentList.Arguments[1]);
                    return !string.IsNullOrWhiteSpace(serviceType) && !string.IsNullOrWhiteSpace(implementationType);
                }

                if (genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    serviceType = ResolveTypeArgumentFqn(semanticModel, genericName.TypeArgumentList.Arguments[0]);
                    if (string.IsNullOrWhiteSpace(serviceType))
                    {
                        return false;
                    }

                    if (invocation.ArgumentList.Arguments.Count == 0)
                    {
                        implementationType = serviceType;
                        return true;
                    }

                    if (TryResolveFactoryRegisteredImplementation(semanticModel, invocation.ArgumentList.Arguments[0].Expression, out implementationType))
                    {
                        return true;
                    }

                    return false;
                }
            }

            if (invocation.ArgumentList.Arguments.Count != 1)
            {
                return false;
            }

            var registeredInstanceType = semanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression).Type;
            if (registeredInstanceType == null)
            {
                return false;
            }

            // Existing instance registrations do not imply constructor dispatch at resolve time.
            return false;
        }

        private static bool TryExtractAutofacRegistration(
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            out string serviceType,
            out string implementationType)
        {
            serviceType = string.Empty;
            implementationType = string.Empty;

            var chain = FlattenInvocationChain(invocation);
            if (chain.Count == 0 || chain[0].Expression is not MemberAccessExpressionSyntax rootMemberAccess)
            {
                return false;
            }

            var rootMethodName = rootMemberAccess.Name.Identifier.ValueText;
            if (rootMethodName == "RegisterType" && rootMemberAccess.Name is GenericNameSyntax registerTypeName)
            {
                if (registerTypeName.TypeArgumentList.Arguments.Count != 1)
                {
                    return false;
                }

                implementationType = ResolveTypeArgumentFqn(semanticModel, registerTypeName.TypeArgumentList.Arguments[0]);
                if (string.IsNullOrWhiteSpace(implementationType))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            var serviceTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in chain.Skip(1))
            {
                if (link.Expression is not MemberAccessExpressionSyntax linkMemberAccess)
                {
                    continue;
                }

                var linkName = linkMemberAccess.Name.Identifier.ValueText;
                if (linkName == "As" && linkMemberAccess.Name is GenericNameSyntax asGenericName)
                {
                    foreach (var typeArgument in asGenericName.TypeArgumentList.Arguments)
                    {
                        var asType = ResolveTypeArgumentFqn(semanticModel, typeArgument);
                        if (!string.IsNullOrWhiteSpace(asType))
                        {
                            serviceTypes.Add(asType);
                        }
                    }
                }
                else if (linkName == "AsSelf")
                {
                    serviceTypes.Add(implementationType);
                }
            }

            if (serviceTypes.Count == 0)
            {
                serviceType = implementationType;
                return true;
            }

            serviceType = serviceTypes.First();
            return true;
        }

        private static List<InvocationExpressionSyntax> FlattenInvocationChain(InvocationExpressionSyntax invocation)
        {
            var chain = new List<InvocationExpressionSyntax>();
            for (InvocationExpressionSyntax? current = invocation; current != null;)
            {
                chain.Add(current);
                if (current.Expression is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Expression is InvocationExpressionSyntax innerInvocation)
                {
                    current = innerInvocation;
                }
                else
                {
                    break;
                }
            }

            chain.Reverse();
            return chain;
        }

        private static string ResolveTypeArgumentFqn(SemanticModel semanticModel, TypeSyntax typeSyntax)
        {
            return semanticModel.GetTypeInfo(typeSyntax).Type?.OriginalDefinition.ToDisplayString() ?? string.Empty;
        }

        private static bool TryResolveFactoryRegisteredImplementation(
            SemanticModel semanticModel,
            ExpressionSyntax expression,
            out string implementationType)
        {
            implementationType = string.Empty;
            var lambda = SymbolExtractor.ExtractLambda(expression);
            if (lambda == null)
            {
                return false;
            }

            ExpressionSyntax? bodyExpression = lambda.Body switch
            {
                ExpressionSyntax directExpression => directExpression,
                BlockSyntax block => block.DescendantNodes().OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression,
                _ => null
            };

            if (bodyExpression == null)
            {
                return false;
            }

            bodyExpression = SymbolExtractor.UnwrapExpression(bodyExpression);
            if (bodyExpression is InvocationExpressionSyntax invocation)
            {
                implementationType = ServiceDispatchExtractor.ResolveRequestedServiceType(semanticModel, invocation, SymbolExtractor.ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation))) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(implementationType);
            }

            if (bodyExpression is ObjectCreationExpressionSyntax objectCreation)
            {
                implementationType = semanticModel.GetTypeInfo(objectCreation).Type?.OriginalDefinition.ToDisplayString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(implementationType);
            }

            return false;
        }

        internal static void AddDispatchTarget(Dictionary<string, HashSet<string>> dispatchMap, string contractFqn, string implementationFqn)
        {
            if (!dispatchMap.TryGetValue(contractFqn, out var targets))
            {
                targets = new HashSet<string>(StringComparer.Ordinal);
                dispatchMap[contractFqn] = targets;
            }

            targets.Add(implementationFqn);
        }
    }
}
