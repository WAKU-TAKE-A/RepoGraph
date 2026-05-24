using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal static class FrameworkConventionDispatcher
    {

        public static void ExtractEntrypoints(IMethodSymbol method, SyntaxNode declarationNode, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var entrypoint in LifecycleConventionExtractor.GetEntrypoints(method))
            {
                var callerFqn = SymbolExtractor.EnsureSyntheticFrameworkSymbol(
                    result,
                    entrypoint.FrameworkCallerFqn,
                    entrypoint.FrameworkCallerName,
                    entrypoint.FrameworkNamespace,
                    entrypoint.FrameworkContainingType,
                    "void",
                    method.Parameters.Length);

                result.MethodCalls.Add(new MethodCallData
                {
                    CallerId = callerFqn,
                    CalleeId = symbolData.Fqn,
                    CallCount = 1,
                    CallType = entrypoint.Rule.CallType,
                    RuleId = entrypoint.Rule.RuleId,
                    RuleFamily = entrypoint.Rule.Family,
                    RuleMode = entrypoint.Rule.ModeName
                });
            }

            foreach (var entrypoint in SerializationConventionExtractor.GetEntrypoints(method, declarationNode))
            {
                var callerFqn = SymbolExtractor.EnsureSyntheticFrameworkSymbol(
                    result,
                    entrypoint.FrameworkCallerFqn,
                    entrypoint.FrameworkCallerName,
                    entrypoint.FrameworkNamespace,
                    entrypoint.FrameworkContainingType,
                    "void",
                    method.Parameters.Length);

                result.MethodCalls.Add(new MethodCallData
                {
                    CallerId = callerFqn,
                    CalleeId = symbolData.Fqn,
                    CallCount = 1,
                    CallType = entrypoint.Rule.CallType,
                    RuleId = entrypoint.Rule.RuleId,
                    RuleFamily = entrypoint.Rule.Family,
                    RuleMode = entrypoint.Rule.ModeName
                });
            }
        }


        public static void ExtractDependencies(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var invocation in SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<InvocationExpressionSyntax>())
            {
                var calledMethod = DirectCallExtractor.ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
                if (SymbolExtractor.TryExtractReflectionConstructorDispatch(compilationCache, semanticModel, node, invocation, calledMethod, symbolData, result))
                {
                    continue;
                }

                var resolutions = ServiceDispatchExtractor.TryExtractServiceResolutionDispatch(compilationCache, semanticModel, invocation, calledMethod).ToList();
                if (resolutions.Count > 0)
                {
                    foreach (var detection in resolutions)
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = detection.TargetConstructorFqn,
                            CallCount = 1,
                            CallType = detection.CallType,
                            RuleId = detection.RuleId,
                            RuleFamily = detection.RuleFamily,
                            RuleMode = detection.RuleMode
                        });
                    }
                    continue;
                }

                if (calledMethod == null)
                {
                    continue;
                }

                var mvvmResolutions = ServiceDispatchExtractor.TryExtractMvvmToolkitMessagingDispatch(compilationCache, calledMethod, symbolData).ToList();
                if (mvvmResolutions.Count > 0)
                {
                    foreach (var detection in mvvmResolutions)
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = detection.TargetReceiveMethodFqn,
                            CallCount = 1,
                            CallType = detection.CallType,
                            RuleId = detection.RuleId,
                            RuleFamily = detection.RuleFamily,
                            RuleMode = detection.RuleMode
                        });
                    }
                    continue;
                }

                var autofacDetection = ServiceDispatchExtractor.TryExtractAutofacModuleDispatch(compilationCache, calledMethod);
                if (autofacDetection != null)
                {
                    foreach (var typeDep in autofacDetection.TypeDependencies)
                    {
                        result.TypeDependencies.Add(new TypeDependencyData
                        {
                            SourceFqn = symbolData.Fqn,
                            TargetFqn = typeDep.TargetModuleFqn,
                            Kind = typeDep.Kind,
                            RuleId = typeDep.RuleId,
                            RuleFamily = typeDep.RuleFamily,
                            RuleMode = typeDep.RuleMode
                        });
                    }

                    foreach (var methodCall in autofacDetection.MethodCalls)
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = methodCall.TargetLoadMethodFqn,
                            CallCount = 1,
                            CallType = methodCall.CallType,
                            RuleId = methodCall.RuleId,
                            RuleFamily = methodCall.RuleFamily,
                            RuleMode = methodCall.RuleMode
                        });
                    }
                    continue;
                }

            }
        }

    }
}
