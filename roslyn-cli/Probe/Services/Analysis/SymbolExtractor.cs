using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;

namespace Probe.Services.Analysis
{
    public class SymbolExtractor
    {
        private readonly ILogger<SymbolExtractor> _logger;
        private readonly ConditionalWeakTable<Compilation, CompilationAnalysisCache> _compilationCache = new();
        private Dictionary<string, HashSet<string>> _solutionServiceRegistrations = new(StringComparer.Ordinal);

        public SymbolExtractor(ILogger<SymbolExtractor> logger)
        {
            _logger = logger;
        }

        public void SetSolutionServiceRegistrations(Dictionary<string, HashSet<string>> registrations)
        {
            _solutionServiceRegistrations = CloneRegistrationMap(registrations);
            _compilationCache.Clear();
        }

        public Dictionary<string, HashSet<string>> CollectServiceRegistrations(Compilation compilation)
        {
            var registrations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            ServiceRegistrationCollector.CollectServiceRegistrations(compilation, registrations);
            return registrations;
        }

        public ExtractionResult Extract(Compilation compilation, SyntaxTree tree)
        {
            var result = new ExtractionResult();
            var semanticModel = compilation.GetSemanticModel(tree);
            var compilationCache = _compilationCache.GetValue(compilation, BuildCompilationCache);
            var root = tree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                ISymbol? symbol = null;

                if (node is FieldDeclarationSyntax fieldDecl)
                {
                    foreach (var variable in fieldDecl.Declaration.Variables)
                    {
                        symbol = semanticModel.GetDeclaredSymbol(variable);
                        if (symbol != null)
                        {
                            ProcessSymbol(compilationCache, semanticModel, variable, symbol, result);
                        }
                    }

                    continue;
                }

                if (node is EventFieldDeclarationSyntax eventFieldDecl)
                {
                    foreach (var variable in eventFieldDecl.Declaration.Variables)
                    {
                        symbol = semanticModel.GetDeclaredSymbol(variable);
                        if (symbol != null)
                        {
                            ProcessSymbol(compilationCache, semanticModel, variable, symbol, result);
                        }
                    }

                    continue;
                }

                if (node is BaseTypeDeclarationSyntax typeDecl)
                    symbol = semanticModel.GetDeclaredSymbol(typeDecl);
                else if (node is MethodDeclarationSyntax methodDecl)
                    symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                else if (node is ConstructorDeclarationSyntax ctorDecl)
                    symbol = semanticModel.GetDeclaredSymbol(ctorDecl);
                else if (node is PropertyDeclarationSyntax propDecl)
                    symbol = semanticModel.GetDeclaredSymbol(propDecl);
                else if (node is EventDeclarationSyntax eventDecl)
                    symbol = semanticModel.GetDeclaredSymbol(eventDecl);
                else if (node is AccessorDeclarationSyntax accessorDecl)
                    symbol = semanticModel.GetDeclaredSymbol(accessorDecl);
                else if (node is LocalFunctionStatementSyntax localFunctionDecl)
                    symbol = semanticModel.GetDeclaredSymbol(localFunctionDecl);

                if (symbol != null)
                {
                    ProcessSymbol(compilationCache, semanticModel, node, symbol, result);

                    if (symbol is INamedTypeSymbol namedTypeSymbol)
                    {
                        if (node is ClassDeclarationSyntax classDeclaration && classDeclaration.ParameterList != null)
                        {
                            ProcessPrimaryConstructorSymbol(namedTypeSymbol, classDeclaration, result);
                        }
                        else if (node is StructDeclarationSyntax structDeclaration && structDeclaration.ParameterList != null)
                        {
                            ProcessPrimaryConstructorSymbol(namedTypeSymbol, structDeclaration, result);
                        }
                    }
                }
            }

            foreach (var anonymousFunction in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
            {
                ProcessAnonymousFunction(compilationCache, semanticModel, anonymousFunction, result);
            }

            // Aggregate call counts
            result.MethodCalls = result.MethodCalls
                .GroupBy(c => new { c.CallerId, c.CalleeId, c.CallType, c.RuleId, c.RuleFamily, c.RuleMode })
                .Select(g => new MethodCallData
                {
                    CallerId = g.Key.CallerId,
                    CalleeId = g.Key.CalleeId,
                    CallCount = g.Sum(x => x.CallCount),
                    CallType = g.Key.CallType,
                    RuleId = g.Key.RuleId,
                    RuleFamily = g.Key.RuleFamily,
                    RuleMode = g.Key.RuleMode
                }).ToList();

            // Deduplicate field accesses (upgrade read to read_write if both read and write exist)
            result.FieldAccesses = DeduplicateFieldAccesses(result.FieldAccesses);
            result.TypeDependencies = result.TypeDependencies
                .GroupBy(d => new { d.SourceFqn, d.TargetFqn, d.Kind, d.RuleId, d.RuleFamily, d.RuleMode })
                .Select(g => g.First())
                .ToList();

            return result;
        }

        private void ProcessSymbol(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, SyntaxNode declarationNode, ISymbol symbol, ExtractionResult result)
        {
            var symbolData = SymbolDataMapper.MapToData(symbol, declarationNode);
            result.Symbols.Add(symbolData);

            if (symbol is INamedTypeSymbol namedType)
            {
                if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
                {
                    result.Inheritances.Add(new InheritanceData
                    {
                        DerivedId = symbolData.Fqn,
                        BaseId = namedType.BaseType.OriginalDefinition.ToDisplayString(),
                        Kind = StructuralEdgeCatalog.Extends
                    });
                }

                foreach (var iface in namedType.Interfaces)
                {
                    result.Inheritances.Add(new InheritanceData
                    {
                        DerivedId = symbolData.Fqn,
                        BaseId = iface.OriginalDefinition.ToDisplayString(),
                        Kind = StructuralEdgeCatalog.Implements
                    });
                }
            }

            if (symbol is IFieldSymbol field)
            {
                TypeDependencyExtractor.RecordTypeDependency(field.Type, symbolData.Fqn, result);
                DelegateReferenceExtractor.ExtractDelegateReferences(semanticModel, declarationNode, symbolData, result);
            }
            else if (symbol is IPropertySymbol prop)
            {
                TypeDependencyExtractor.RecordTypeDependency(prop.Type, symbolData.Fqn, result);
            }
            else if (symbol is IMethodSymbol method)
            {
                TypeDependencyExtractor.RecordTypeDependency(method.ReturnType, symbolData.Fqn, result);
                foreach (var param in method.Parameters)
                {
                    TypeDependencyExtractor.RecordTypeDependency(param.Type, symbolData.Fqn, result);
                }

                ExtractOverrideDispatch(compilationCache, method, symbolData, result);
                FrameworkConventionDispatcher.ExtractEntrypoints(method, declarationNode, symbolData, result);
                
            }

            if ((symbol is IMethodSymbol && IsExecutableNode(declarationNode))
                || (symbol is IPropertySymbol && HasPropertyExecutableBody(declarationNode)))
            {
                DirectCallExtractor.ExtractMethodCalls(compilationCache, semanticModel, declarationNode, symbolData, result);
                FrameworkConventionDispatcher.ExtractDependencies(compilationCache, semanticModel, declarationNode, symbolData, result);
                ThreadBoundaryExtractor.ExtractThreadBoundaries(semanticModel, declarationNode, symbolData);
                FieldAccessExtractor.ExtractFieldAccesses(semanticModel, declarationNode, symbolData, result);
                EventSubscriptionExtractor.ExtractEventSubscriptions(semanticModel, declarationNode, symbolData, result);
                DelegateReferenceExtractor.ExtractDelegateReferences(semanticModel, declarationNode, symbolData, result);
                TypeDependencyExtractor.ExtractTypeDependencies(semanticModel, declarationNode, symbolData, result);
            }
        }

        private void ProcessPrimaryConstructorSymbol(INamedTypeSymbol typeSymbol, TypeDeclarationSyntax typeDeclaration, ExtractionResult result)
        {
            var parameterCount = typeDeclaration.ParameterList?.Parameters.Count ?? 0;
            var constructorSymbol = typeSymbol.InstanceConstructors
                .FirstOrDefault(ctor => ctor.Parameters.Length == parameterCount);
            if (constructorSymbol == null)
            {
                return;
            }

            var symbolData = SymbolDataMapper.MapToData(constructorSymbol, typeDeclaration);
            if (result.Symbols.Any(existing => existing.Fqn == symbolData.Fqn))
            {
                return;
            }

            result.Symbols.Add(symbolData);
            foreach (var parameter in constructorSymbol.Parameters)
            {
                TypeDependencyExtractor.RecordTypeDependency(parameter.Type, symbolData.Fqn, result);
            }
        }

        private void ProcessAnonymousFunction(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, AnonymousFunctionExpressionSyntax anonymousFunction, ExtractionResult result)
        {
            var enclosingSymbol = GetContainingExecutableMethod(semanticModel.GetEnclosingSymbol(anonymousFunction.SpanStart));
            if (enclosingSymbol == null)
            {
                return;
            }

            var symbolData = SymbolDataMapper.CreateAnonymousFunctionData(enclosingSymbol, anonymousFunction);
            result.Symbols.Add(symbolData);

            DirectCallExtractor.ExtractMethodCalls(compilationCache, semanticModel, anonymousFunction, symbolData, result);
            FrameworkConventionDispatcher.ExtractDependencies(compilationCache, semanticModel, anonymousFunction, symbolData, result);
            ThreadBoundaryExtractor.ExtractThreadBoundaries(semanticModel, anonymousFunction, symbolData);
            FieldAccessExtractor.ExtractFieldAccesses(semanticModel, anonymousFunction, symbolData, result);
            EventSubscriptionExtractor.ExtractEventSubscriptions(semanticModel, anonymousFunction, symbolData, result);
            DelegateReferenceExtractor.ExtractDelegateReferences(semanticModel, anonymousFunction, symbolData, result);
            TypeDependencyExtractor.ExtractTypeDependencies(semanticModel, anonymousFunction, symbolData, result);
            PromoteAnonymousFunctionCalls(enclosingSymbol, symbolData, result);
        }



        private static bool IsExecutableNode(SyntaxNode node)
        {
            return node is MethodDeclarationSyntax
                || node is ConstructorDeclarationSyntax
                || node is AccessorDeclarationSyntax
                || node is LocalFunctionStatementSyntax;
        }

        private static bool HasPropertyExecutableBody(SyntaxNode node)
        {
            if (node is not PropertyDeclarationSyntax property)
            {
                return false;
            }

            return property.ExpressionBody != null;
        }

        private static void ExtractOverrideDispatch(
            CompilationAnalysisCache compilationCache,
            IMethodSymbol method,
            SymbolData symbolData,
            ExtractionResult result)
        {
            if (method.OverriddenMethod == null)
            {
                return;
            }

            var baseFqn = method.OverriddenMethod.OriginalDefinition.ToDisplayString();
            if (!compilationCache.KnowsMethod(baseFqn))
            {
                result.Symbols.Add(SymbolDataMapper.CreateFrameworkMethodSymbol(method.OverriddenMethod));
            }

            result.MethodCalls.Add(new MethodCallData
            {
                CallerId = baseFqn,
                CalleeId = symbolData.Fqn,
                CallCount = 1,
                CallType = StructuralEdgeCatalog.OverrideDispatch
            });
        }


        /// <summary>
        /// Extract method invocation calls.
        /// </summary>

        /// <summary>
        /// Detect thread boundary patterns in method body:
        /// - Invoke/BeginInvoke (UI thread dispatch)
        /// - Task.Run / Task.Factory.StartNew (background thread spawn)
        /// - BackgroundWorker usage
        /// - Application.DoEvents() (re-entrancy hazard)
        /// - lock statements (mutual exclusion)
        /// </summary>

        /// <summary>
        /// Extract all field and property accesses within a method body.
        /// Tracks read vs write, and whether the access is to an external class.
        /// </summary>


        private string DetermineAccessKind(SyntaxNode node)
        {
            var parent = node.Parent;

            // Direct assignment: x = value
            if (parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
                return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "write" : "read_write";

            // Member access on left of assignment: obj.Field = value
            if (parent is MemberAccessExpressionSyntax memberAccess)
            {
                var grandParent = memberAccess.Parent;
                if (grandParent is AssignmentExpressionSyntax outerAssignment && outerAssignment.Left == memberAccess)
                    return outerAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "write" : "read_write";
            }

            // Prefix/postfix increment/decrement: x++ / --x
            if (parent is PostfixUnaryExpressionSyntax || parent is PrefixUnaryExpressionSyntax)
            {
                var kind = parent.Kind();
                if (kind == SyntaxKind.PostIncrementExpression || kind == SyntaxKind.PostDecrementExpression ||
                    kind == SyntaxKind.PreIncrementExpression || kind == SyntaxKind.PreDecrementExpression)
                    return "read_write";
            }

            // ref / out argument
            if (parent is ArgumentSyntax arg)
            {
                if (arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword))
                    return "write";
                if (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword))
                    return "read_write";
            }

            return "read";
        }

        /// <summary>
        /// Detect event handler subscriptions (+=) and unsubscriptions (-=).
        /// Records them as special method calls with type "event_subscribe" / "event_unsubscribe".
        /// </summary>

        private static string? GetEventDispatchCallerFqn(SemanticModel semanticModel, IEventSymbol eventSymbol, ExtractionResult result)
        {
            var eventFqn = eventSymbol.ToDisplayString();
            var eventNamespace = eventSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            var eventType = eventSymbol.ContainingType?.ToDisplayString();

            if (!LooksLikeFrameworkOwnedSymbol(eventNamespace, eventType))
            {
                return eventFqn;
            }

            return EnsureSyntheticFrameworkSymbol(
                result,
                $"framework::{eventFqn}",
                eventSymbol.Name,
                "Framework.Events",
                eventType,
                eventSymbol.Type?.ToDisplayString(),
                eventSymbol.Type is INamedTypeSymbol namedType ? namedType.DelegateInvokeMethod?.Parameters.Length ?? 0 : 0);
        }



        internal static IEnumerable<SyntaxNode> GetAnalysisDescendantNodes(SyntaxNode node)
        {
            if (node is AnonymousFunctionExpressionSyntax anonymousFunction)
            {
                if (anonymousFunction.Body is CSharpSyntaxNode bodyNode)
                {
                    return bodyNode.DescendantNodesAndSelf(descendIntoChildren: child => !IsNestedExecutableBoundary(child));
                }

                return Enumerable.Empty<SyntaxNode>();
            }

            return node.DescendantNodes(descendIntoChildren: child => !IsNestedExecutableBoundary(child));
        }

        private static bool IsNestedExecutableBoundary(SyntaxNode node)
        {
            return node is LocalFunctionStatementSyntax || node is AnonymousFunctionExpressionSyntax;
        }

        /// <summary>
        /// Deduplicate field accesses: if both read and write exist for same accessor+target, merge to read_write.
        /// </summary>
        private List<FieldAccessData> DeduplicateFieldAccesses(List<FieldAccessData> accesses)
        {
            var grouped = accesses.GroupBy(a => new { a.AccessorFqn, a.TargetFqn, a.IsExternal });
            var deduped = new List<FieldAccessData>();

            foreach (var group in grouped)
            {
                var kinds = group.Select(a => a.AccessKind).Distinct().ToList();
                var mergedKind = (kinds.Contains("read") && kinds.Contains("write")) ? "read_write" : kinds.First();

                deduped.Add(new FieldAccessData
                {
                    AccessorFqn = group.Key.AccessorFqn,
                    TargetFqn = group.Key.TargetFqn,
                    AccessKind = mergedKind,
                    IsExternal = group.Key.IsExternal
                });
            }

            return deduped;
        }

        /// <summary>
        /// Check if a type is or derives from a given fully qualified base type name.
        /// </summary>
        internal static bool IsOrDerivedFrom(ITypeSymbol? type, string baseTypeFqn)
        {
            var current = type;
            while (current != null)
            {
                if (string.Equals(current.ToDisplayString(), baseTypeFqn, StringComparison.Ordinal) ||
                    string.Equals(current.OriginalDefinition.ToDisplayString(), baseTypeFqn, StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.BaseType;
            }
            return false;
        }

        internal static bool LooksLikeFrameworkOwnedSymbol(string symbolNamespace, string? containingType)
        {
            if (symbolNamespace.StartsWith("System.", StringComparison.Ordinal) ||
                symbolNamespace.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                symbolNamespace.StartsWith("Windows.", StringComparison.Ordinal))
            {
                return true;
            }

            return containingType != null && (
                containingType.StartsWith("System.", StringComparison.Ordinal) ||
                containingType.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                containingType.StartsWith("Windows.", StringComparison.Ordinal));
        }



        internal static string EnsureSyntheticFrameworkSymbol(
            ExtractionResult result,
            string fqn,
            string name,
            string frameworkNamespace,
            string? containingType,
            string? returnType,
            int parameterCount)
        {
            if (!result.Symbols.Any(symbol => string.Equals(symbol.Fqn, fqn, StringComparison.Ordinal)))
            {
                result.Symbols.Add(new SymbolData
                {
                    Id = GetStableHash(fqn),
                    Fqn = fqn,
                    Name = name,
                    Kind = SyntheticSymbolKindCatalog.FrameworkMethod,
                    Namespace = frameworkNamespace,
                    ContainingType = containingType,
                    Accessibility = "public",
                    IsStatic = true,
                    IsAsync = false,
                    ParameterCount = parameterCount,
                    ReturnType = returnType,
                });
            }

            return fqn;
        }

        private static bool ShouldExpandDynamicDispatch(InvocationExpressionSyntax invocation, IMethodSymbol calledMethod)
        {
            return calledMethod.IsAbstract || calledMethod.ContainingType?.TypeKind == TypeKind.Interface;
        }








        internal static bool TryExtractReflectionConstructorDispatch(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            InvocationExpressionSyntax invocation,
            IMethodSymbol? calledMethod,
            SymbolData symbolData,
            ExtractionResult result)
        {
            if (LooksLikeGetConstructorSyntax(invocation) &&
                invocation.Expression is MemberAccessExpressionSyntax getConstructorAccess)
            {
                var lookupParameterTypes = ResolveReflectedTypeDescriptorArguments(
                    semanticModel,
                    scopeNode,
                    invocation.SpanStart,
                    invocation.ArgumentList.Arguments)
                    .ToList();

                if (lookupParameterTypes.Count > 0 &&
                    TryResolveReflectedConstructorTargets(
                        compilationCache,
                        semanticModel,
                        scopeNode,
                        invocation.SpanStart,
                        getConstructorAccess.Expression,
                        lookupParameterTypes,
                        out var lookupTargets))
                {
                    var lookupRuleMetadata = lookupTargets.Count > 1
                        ? FrameworkRuleCatalog.ReflectionConstructorCandidate
                        : FrameworkRuleCatalog.ReflectionConstructorDispatch;

                    foreach (var target in lookupTargets)
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = target,
                            CallCount = 1,
                            CallType = lookupRuleMetadata.CallType,
                            RuleId = lookupRuleMetadata.RuleId,
                            RuleFamily = lookupRuleMetadata.Family,
                            RuleMode = lookupRuleMetadata.ModeName
                        });
                    }

                    return true;
                }
            }

            if (IsConstructorInfoInvoke(calledMethod) || LooksLikeConstructorInvokeSyntax(invocation))
            {
                if (TryResolveConstructorInfoDispatch(
                    compilationCache,
                    semanticModel,
                    scopeNode,
                    invocation,
                    symbolData,
                    out var constructorTargets))
                {
                    var constructorRuleMetadata = constructorTargets.Count > 1
                        ? FrameworkRuleCatalog.ReflectionConstructorCandidate
                        : FrameworkRuleCatalog.ReflectionConstructorDispatch;

                    foreach (var target in constructorTargets)
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = target,
                            CallCount = 1,
                            CallType = constructorRuleMetadata.CallType,
                            RuleId = constructorRuleMetadata.RuleId,
                            RuleFamily = constructorRuleMetadata.Family,
                            RuleMode = constructorRuleMetadata.ModeName
                        });
                    }
                }

                return true;
            }

            if (!IsActivatorCreateInstance(calledMethod) &&
                !LooksLikeActivatorCreateInstanceSyntax(invocation) ||
                invocation.ArgumentList.Arguments.Count == 0)
            {
                return false;
            }

            var typeArgument = invocation.ArgumentList.Arguments[0].Expression;
            var parameterTypes = ResolveRuntimeArgumentTypes(semanticModel, invocation.ArgumentList.Arguments.Skip(1)).ToList();
            if (!TryResolveReflectedConstructorTargets(
                compilationCache,
                semanticModel,
                scopeNode,
                invocation.SpanStart,
                typeArgument,
                parameterTypes,
                out var activatorTargets))
            {
                return false;
            }

            var activatorRuleMetadata = activatorTargets.Count > 1
                ? FrameworkRuleCatalog.ReflectionConstructorCandidate
                : FrameworkRuleCatalog.ReflectionConstructorDispatch;

            foreach (var target in activatorTargets)
            {
                result.MethodCalls.Add(new MethodCallData
                {
                    CallerId = symbolData.Fqn,
                    CalleeId = target,
                    CallCount = 1,
                    CallType = activatorRuleMetadata.CallType,
                    RuleId = activatorRuleMetadata.RuleId,
                    RuleFamily = activatorRuleMetadata.Family,
                    RuleMode = activatorRuleMetadata.ModeName
                });
            }

            return true;
        }

        private static bool TryResolveConstructorInfoDispatch(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            InvocationExpressionSyntax invocation,
            SymbolData symbolData,
            out IReadOnlyCollection<string> constructorTargets)
        {
            constructorTargets = Array.Empty<string>();
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax constructorIdentifier)
            {
                return false;
            }

            if (!TryFindLatestAssignmentExpression(scopeNode, constructorIdentifier.Identifier.ValueText, invocation.SpanStart, out var assignedExpression))
            {
                return false;
            }

            if (assignedExpression is not InvocationExpressionSyntax constructorLookup ||
                constructorLookup.Expression is not MemberAccessExpressionSyntax constructorLookupAccess ||
                !string.Equals(constructorLookupAccess.Name.Identifier.ValueText, "GetConstructor", StringComparison.Ordinal))
            {
                return false;
            }

            if (constructorLookup.ArgumentList.Arguments.Count == 0)
            {
                return false;
            }

            var typeExpression = constructorLookupAccess.Expression;
            var parameterTypes = ResolveReflectedTypeDescriptorArguments(
                semanticModel,
                scopeNode,
                invocation.SpanStart,
                constructorLookup.ArgumentList.Arguments)
                .ToList();

            if (parameterTypes.Count == 0)
            {
                return false;
            }

            if (!TryResolveReflectedConstructorTargets(
                compilationCache,
                semanticModel,
                scopeNode,
                invocation.SpanStart,
                typeExpression,
                parameterTypes,
                out var targets))
            {
                return false;
            }

            constructorTargets = targets;
            return constructorTargets.Count > 0;
        }

        private static bool TryResolveReflectedConstructorTargets(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            int usagePosition,
            ExpressionSyntax typeExpression,
            IReadOnlyList<string> parameterTypes,
            out IReadOnlyCollection<string> constructorTargets)
        {
            constructorTargets = Array.Empty<string>();
            var candidateTypes = ResolveReflectedTypeCandidates(compilationCache, semanticModel, scopeNode, usagePosition, typeExpression);
            if (candidateTypes.Count == 0)
            {
                return false;
            }

            var resolvedTargets = compilationCache
                .GetConstructorCandidates(candidateTypes, parameterTypes)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (resolvedTargets.Count == 0)
            {
                return false;
            }

            constructorTargets = resolvedTargets;
            return true;
        }

        private static HashSet<string> ResolveReflectedTypeCandidates(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            int usagePosition,
            ExpressionSyntax expression)
        {
            expression = UnwrapExpression(expression);

            if (expression is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.ValueText;
                if (string.Equals(methodName, "GetTypes", StringComparison.Ordinal) ||
                    string.Equals(methodName, "GetExportedTypes", StringComparison.Ordinal))
                {
                    return compilationCache.GetAllTypeFqns();
                }

                if (string.Equals(methodName, "Where", StringComparison.Ordinal))
                {
                    var source = ResolveReflectedTypeCandidates(compilationCache, semanticModel, scopeNode, usagePosition, memberAccess.Expression);
                    if (source.Count == 0 || invocation.ArgumentList.Arguments.Count == 0)
                    {
                        return source;
                    }

                    var lambda = ExtractLambda(invocation.ArgumentList.Arguments[0].Expression);
                    if (lambda == null)
                    {
                        return source;
                    }

                    var parameterName = GetSingleLambdaParameterName(lambda);
                    if (string.IsNullOrWhiteSpace(parameterName))
                    {
                        return source;
                    }

                    var filtered = source
                        .Where(typeFqn => compilationCache.TryGetTypeMetadata(typeFqn, out var metadata) &&
                                          EvaluateTypePredicate(compilationCache, semanticModel, lambda.Body, parameterName, metadata))
                        .ToHashSet(StringComparer.Ordinal);
                    return filtered;
                }

                if (string.Equals(methodName, "Select", StringComparison.Ordinal) ||
                    string.Equals(methodName, "ToList", StringComparison.Ordinal) ||
                    string.Equals(methodName, "ToArray", StringComparison.Ordinal) ||
                    string.Equals(methodName, "AsEnumerable", StringComparison.Ordinal))
                {
                    return ResolveReflectedTypeCandidates(compilationCache, semanticModel, scopeNode, usagePosition, memberAccess.Expression);
                }

                if (string.Equals(methodName, "First", StringComparison.Ordinal) ||
                    string.Equals(methodName, "FirstOrDefault", StringComparison.Ordinal) ||
                    string.Equals(methodName, "Single", StringComparison.Ordinal) ||
                    string.Equals(methodName, "SingleOrDefault", StringComparison.Ordinal) ||
                    string.Equals(methodName, "Last", StringComparison.Ordinal) ||
                    string.Equals(methodName, "LastOrDefault", StringComparison.Ordinal) ||
                    string.Equals(methodName, "ElementAt", StringComparison.Ordinal) ||
                    string.Equals(methodName, "ElementAtOrDefault", StringComparison.Ordinal))
                {
                    return ResolveReflectedTypeCandidates(compilationCache, semanticModel, scopeNode, usagePosition, memberAccess.Expression);
                }
            }

            if (expression is IdentifierNameSyntax identifier)
            {
                var declaredSymbol = semanticModel.GetSymbolInfo(identifier).Symbol;
                if (declaredSymbol is IParameterSymbol parameter && parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
                {
                    var lambda = identifier.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();
                    var selectInvocation = lambda?
                        .Ancestors()
                        .OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault(candidate =>
                            candidate.Expression is MemberAccessExpressionSyntax candidateAccess &&
                            string.Equals(candidateAccess.Name.Identifier.ValueText, "Select", StringComparison.Ordinal) &&
                            candidate.ArgumentList.Arguments.Any(argument => argument.Expression == lambda));
                    if (selectInvocation?.Expression is MemberAccessExpressionSyntax selectAccess)
                    {
                        return ResolveReflectedTypeCandidates(compilationCache, semanticModel, scopeNode, usagePosition, selectAccess.Expression);
                    }
                }

                if (declaredSymbol is ILocalSymbol localSymbol)
                {
                    foreach (var declaringSyntax in localSymbol.DeclaringSyntaxReferences)
                    {
                        var declaringNode = declaringSyntax.GetSyntax();
                        if (declaringNode is ForEachStatementSyntax forEachStatement)
                        {
                            var sourceTypes = ResolveReflectedTypeCandidates(
                                compilationCache,
                                semanticModel,
                                scopeNode,
                                usagePosition,
                                forEachStatement.Expression);

                            return ApplyForeachTypeGuards(
                                compilationCache,
                                semanticModel,
                                forEachStatement,
                                usagePosition,
                                localSymbol.Name,
                                sourceTypes);
                        }
                    }
                }

                if (TryFindLatestAssignmentExpression(scopeNode, identifier.Identifier.ValueText, usagePosition, out var assignedExpression))
                {
                    return ResolveReflectedTypeCandidates(compilationCache, semanticModel, scopeNode, assignedExpression.SpanStart, assignedExpression);
                }
            }

            if (expression is InvocationExpressionSyntax getTypeInvocation &&
                getTypeInvocation.Expression is MemberAccessExpressionSyntax getTypeAccess &&
                string.Equals(getTypeAccess.Name.Identifier.ValueText, "GetType", StringComparison.Ordinal) &&
                getTypeInvocation.ArgumentList.Arguments.Count == 0)
            {
                var receiverType = ResolveEffectiveTypeSymbol(semanticModel, scopeNode, semanticModel.GetTypeInfo(getTypeAccess.Expression).Type);
                if (receiverType != null)
                {
                    return compilationCache.GetConcreteTypesAssignableTo(receiverType.OriginalDefinition.ToDisplayString());
                }

                return ResolveReflectedTypeCandidates(
                    compilationCache,
                    semanticModel,
                    scopeNode,
                    usagePosition,
                    getTypeAccess.Expression);
            }

            if (expression is ElementAccessExpressionSyntax elementAccess)
            {
                var elementType = ResolveEffectiveTypeSymbol(semanticModel, scopeNode, semanticModel.GetTypeInfo(elementAccess).Type);
                if (elementType != null)
                {
                    return compilationCache.GetConcreteTypesAssignableTo(elementType.OriginalDefinition.ToDisplayString());
                }
            }

            if (expression is TypeOfExpressionSyntax typeOfExpression)
            {
                var type = semanticModel.GetTypeInfo(typeOfExpression.Type).Type;
                if (type != null)
                {
                    return new HashSet<string>(StringComparer.Ordinal) { type.OriginalDefinition.ToDisplayString() };
                }
            }

            return new HashSet<string>(StringComparer.Ordinal);
        }

        private static bool TryFindLatestAssignmentExpression(
            SyntaxNode scopeNode,
            string identifier,
            int usagePosition,
            out ExpressionSyntax expression)
        {
            expression = null!;
            ExpressionSyntax? latest = null;
            var latestPosition = -1;

            foreach (var searchScope in GetAssignmentSearchScopes(scopeNode))
            {
                foreach (var declarator in GetAnalysisDescendantNodes(searchScope).OfType<VariableDeclaratorSyntax>())
                {
                    if (!string.Equals(declarator.Identifier.ValueText, identifier, StringComparison.Ordinal) ||
                        declarator.SpanStart >= usagePosition ||
                        declarator.Initializer == null)
                    {
                        continue;
                    }

                    if (declarator.SpanStart > latestPosition)
                    {
                        latest = declarator.Initializer.Value;
                        latestPosition = declarator.SpanStart;
                    }
                }

                foreach (var assignment in GetAnalysisDescendantNodes(searchScope).OfType<AssignmentExpressionSyntax>())
                {
                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                        assignment.SpanStart >= usagePosition ||
                        assignment.Left is not IdentifierNameSyntax leftIdentifier ||
                        !string.Equals(leftIdentifier.Identifier.ValueText, identifier, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (assignment.SpanStart > latestPosition)
                    {
                        latest = assignment.Right;
                        latestPosition = assignment.SpanStart;
                    }
                }
            }

            if (latest == null)
            {
                return false;
            }

            expression = latest;
            return true;
        }

        private static IEnumerable<SyntaxNode> GetAssignmentSearchScopes(SyntaxNode scopeNode)
        {
            for (SyntaxNode? current = scopeNode; current != null; current = current.Parent)
            {
                switch (current)
                {
                    case AnonymousFunctionExpressionSyntax anonymousFunction:
                        yield return anonymousFunction;
                        break;

                    case BaseMethodDeclarationSyntax methodDeclaration:
                        yield return methodDeclaration;
                        break;

                    case LocalFunctionStatementSyntax localFunction:
                        yield return localFunction;
                        break;

                    case AccessorDeclarationSyntax accessor:
                        yield return accessor;
                        break;
                }
            }
        }

        private static IEnumerable<string> ResolveReflectedTypeDescriptorArguments(
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            int usagePosition,
            IEnumerable<ArgumentSyntax> arguments)
        {
            foreach (var argument in arguments)
            {
                foreach (var typeName in ResolveTypeDescriptorNamesFromExpression(semanticModel, scopeNode, usagePosition, argument.Expression))
                {
                    yield return typeName;
                }
            }
        }

        private static IEnumerable<string> ResolveTypeDescriptorNamesFromExpression(
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            int usagePosition,
            ExpressionSyntax expression)
        {
            expression = UnwrapExpression(expression);

            if (expression is IdentifierNameSyntax identifier &&
                TryFindLatestAssignmentExpression(scopeNode, identifier.Identifier.ValueText, usagePosition, out var assignedExpression))
            {
                foreach (var typeName in ResolveTypeDescriptorNamesFromExpression(semanticModel, scopeNode, assignedExpression.SpanStart, assignedExpression))
                {
                    yield return typeName;
                }

                yield break;
            }

            if (expression is ArrayCreationExpressionSyntax arrayCreation && arrayCreation.Initializer != null)
            {
                foreach (var item in arrayCreation.Initializer.Expressions)
                {
                    foreach (var typeName in ResolveTypeDescriptorNamesFromExpression(semanticModel, scopeNode, usagePosition, item))
                    {
                        yield return typeName;
                    }
                }

                yield break;
            }

            if (expression is ImplicitArrayCreationExpressionSyntax implicitArray && implicitArray.Initializer != null)
            {
                foreach (var item in implicitArray.Initializer.Expressions)
                {
                    foreach (var typeName in ResolveTypeDescriptorNamesFromExpression(semanticModel, scopeNode, usagePosition, item))
                    {
                        yield return typeName;
                    }
                }

                yield break;
            }

            if (expression is TypeOfExpressionSyntax typeOfExpression)
            {
                var type = semanticModel.GetTypeInfo(typeOfExpression.Type).Type;
                if (type != null)
                {
                    yield return type.OriginalDefinition.ToDisplayString();
                }
            }
        }

        private static IEnumerable<string> ResolveRuntimeArgumentTypes(SemanticModel semanticModel, IEnumerable<ArgumentSyntax> arguments)
        {
            foreach (var argument in arguments)
            {
                var type = semanticModel.GetTypeInfo(argument.Expression).Type;
                if (type != null)
                {
                    yield return type.OriginalDefinition.ToDisplayString();
                }
            }
        }

        private static ITypeSymbol? ResolveEffectiveTypeSymbol(
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            ITypeSymbol? typeSymbol)
        {
            while (typeSymbol is ITypeParameterSymbol typeParameter &&
                   TryResolveConstructedTypeParameter(semanticModel, scopeNode, typeParameter, out var resolvedType))
            {
                typeSymbol = resolvedType;
            }

            return typeSymbol;
        }

        private static bool TryResolveConstructedTypeParameter(
            SemanticModel semanticModel,
            SyntaxNode scopeNode,
            ITypeParameterSymbol typeParameter,
            out ITypeSymbol resolvedType)
        {
            resolvedType = null!;
            if (typeParameter.ContainingSymbol is not INamedTypeSymbol declaringType)
            {
                return false;
            }

            var currentType = scopeNode
                .AncestorsAndSelf()
                .OfType<TypeDeclarationSyntax>()
                .Select(typeDeclaration => semanticModel.GetDeclaredSymbol(typeDeclaration))
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();

            for (var candidate = currentType; candidate != null; candidate = candidate.BaseType)
            {
                if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, declaringType.OriginalDefinition))
                {
                    continue;
                }

                var parameterIndex = -1;
                for (var i = 0; i < declaringType.TypeParameters.Length; i++)
                {
                    if (SymbolEqualityComparer.Default.Equals(declaringType.TypeParameters[i], typeParameter))
                    {
                        parameterIndex = i;
                        break;
                    }
                }

                if (parameterIndex < 0 || parameterIndex >= candidate.TypeArguments.Length)
                {
                    return false;
                }

                resolvedType = candidate.TypeArguments[parameterIndex];
                return true;
            }

            return false;
        }

        private static HashSet<string> ApplyForeachTypeGuards(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            ForEachStatementSyntax forEachStatement,
            int usagePosition,
            string parameterName,
            HashSet<string> sourceTypes)
        {
            if (sourceTypes.Count == 0 || forEachStatement.Statement is not BlockSyntax block)
            {
                return sourceTypes;
            }

            var filtered = new HashSet<string>(sourceTypes, StringComparer.Ordinal);
            foreach (var statement in block.Statements)
            {
                if (statement.SpanStart >= usagePosition)
                {
                    break;
                }

                if (statement is not IfStatementSyntax ifStatement ||
                    !IsContinueOnly(ifStatement.Statement))
                {
                    continue;
                }

                var keepCondition = NegateCondition(ifStatement.Condition);
                filtered.RemoveWhere(typeFqn =>
                    !compilationCache.TryGetTypeMetadata(typeFqn, out var metadata) ||
                    !EvaluateTypePredicateExpression(compilationCache, semanticModel, keepCondition, parameterName, metadata));
            }

            return filtered;
        }

        private static bool IsContinueOnly(StatementSyntax statement)
        {
            if (statement is ContinueStatementSyntax)
            {
                return true;
            }

            return statement is BlockSyntax block &&
                   block.Statements.Count == 1 &&
                   block.Statements[0] is ContinueStatementSyntax;
        }

        private static ExpressionSyntax NegateCondition(ExpressionSyntax condition)
        {
            condition = UnwrapExpression(condition);
            if (condition is PrefixUnaryExpressionSyntax prefix &&
                prefix.IsKind(SyntaxKind.LogicalNotExpression))
            {
                return (ExpressionSyntax)UnwrapExpression(prefix.Operand);
            }

            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(condition));
        }

        internal static AnonymousFunctionExpressionSyntax? ExtractLambda(ExpressionSyntax expression)
        {
            expression = UnwrapExpression(expression);
            return expression as AnonymousFunctionExpressionSyntax;
        }

        private static string? GetSingleLambdaParameterName(AnonymousFunctionExpressionSyntax lambda)
        {
            return lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                AnonymousMethodExpressionSyntax anonymousMethod when anonymousMethod.ParameterList?.Parameters.Count == 1 => anonymousMethod.ParameterList.Parameters[0].Identifier.ValueText,
                _ => null
            };
        }

        private static bool EvaluateTypePredicate(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            CSharpSyntaxNode body,
            string parameterName,
            ReflectionTypeMetadata metadata)
        {
            if (body is BlockSyntax block)
            {
                var returnStatement = block.DescendantNodes().OfType<ReturnStatementSyntax>().FirstOrDefault();
                return returnStatement?.Expression != null &&
                       EvaluateTypePredicateExpression(compilationCache, semanticModel, returnStatement.Expression, parameterName, metadata);
            }

            return EvaluateTypePredicateExpression(compilationCache, semanticModel, body, parameterName, metadata);
        }

        private static bool EvaluateTypePredicateExpression(
            CompilationAnalysisCache compilationCache,
            SemanticModel semanticModel,
            SyntaxNode expression,
            string parameterName,
            ReflectionTypeMetadata metadata)
        {
            if (expression is ExpressionSyntax expressionSyntax)
            {
                expression = UnwrapExpression(expressionSyntax);
            }

            switch (expression)
            {
                case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
                    return !EvaluateTypePredicateExpression(compilationCache, semanticModel, prefix.Operand, parameterName, metadata);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                    return EvaluateTypePredicateExpression(compilationCache, semanticModel, binary.Left, parameterName, metadata)
                        && EvaluateTypePredicateExpression(compilationCache, semanticModel, binary.Right, parameterName, metadata);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression):
                    return EvaluateTypePredicateExpression(compilationCache, semanticModel, binary.Left, parameterName, metadata)
                        || EvaluateTypePredicateExpression(compilationCache, semanticModel, binary.Right, parameterName, metadata);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression) || binary.IsKind(SyntaxKind.EqualsExpression):
                    if (TryResolveTypeMemberString(binary.Left, parameterName, metadata, out var leftValue) &&
                        TryResolveStringLiteral(binary.Right, out var rightValue))
                    {
                        return binary.IsKind(SyntaxKind.EqualsExpression)
                            ? string.Equals(leftValue, rightValue, StringComparison.Ordinal)
                            : !string.Equals(leftValue, rightValue, StringComparison.Ordinal);
                    }

                    if (TryResolveHasConstructorPredicate(binary.Left, parameterName, metadata, out var leftHasConstructor) &&
                        IsNullLiteral(binary.Right))
                    {
                        return binary.IsKind(SyntaxKind.EqualsExpression)
                            ? !leftHasConstructor
                            : leftHasConstructor;
                    }

                    if (TryResolveHasConstructorPredicate(binary.Right, parameterName, metadata, out var rightHasConstructor) &&
                        IsNullLiteral(binary.Left))
                    {
                        return binary.IsKind(SyntaxKind.EqualsExpression)
                            ? !rightHasConstructor
                            : rightHasConstructor;
                    }

                    break;

                case InvocationExpressionSyntax invocation:
                    if (TryEvaluateAssignableFromPredicate(semanticModel, invocation, parameterName, metadata, out var isAssignable))
                    {
                        return isAssignable;
                    }

                    break;

                case MemberAccessExpressionSyntax memberAccess:
                    if (TryResolveTypeMemberBoolean(memberAccess, parameterName, metadata, out var memberValue))
                    {
                        return memberValue;
                    }

                    break;
            }

            return true;
        }

        private static bool TryResolveTypeMemberBoolean(
            MemberAccessExpressionSyntax memberAccess,
            string parameterName,
            ReflectionTypeMetadata metadata,
            out bool value)
        {
            value = false;
            if (memberAccess.Expression is not IdentifierNameSyntax identifier ||
                !string.Equals(identifier.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                return false;
            }

            value = memberAccess.Name.Identifier.ValueText switch
            {
                "IsInterface" => metadata.IsInterface,
                "IsAbstract" => metadata.IsAbstract,
                "IsClass" => metadata.IsClass,
                _ => value
            };

            return memberAccess.Name.Identifier.ValueText is "IsInterface" or "IsAbstract" or "IsClass";
        }

        private static bool TryResolveHasConstructorPredicate(
            SyntaxNode expression,
            string parameterName,
            ReflectionTypeMetadata metadata,
            out bool hasConstructor)
        {
            hasConstructor = false;
            if (expression is ExpressionSyntax expressionSyntax)
            {
                expression = UnwrapExpression(expressionSyntax);
            }

            if (expression is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                !string.Equals(memberAccess.Name.Identifier.ValueText, "GetConstructor", StringComparison.Ordinal) ||
                memberAccess.Expression is not IdentifierNameSyntax identifier ||
                !string.Equals(identifier.Identifier.ValueText, parameterName, StringComparison.Ordinal) ||
                invocation.ArgumentList.Arguments.Count != 1)
            {
                return false;
            }

            var argumentExpression = UnwrapExpression(invocation.ArgumentList.Arguments[0].Expression);
            var requestsEmptyTypes =
                argumentExpression is MemberAccessExpressionSyntax member &&
                member.Expression is IdentifierNameSyntax typeIdentifier &&
                string.Equals(typeIdentifier.Identifier.ValueText, "Type", StringComparison.Ordinal) &&
                string.Equals(member.Name.Identifier.ValueText, "EmptyTypes", StringComparison.Ordinal);

            if (!requestsEmptyTypes)
            {
                return false;
            }

            hasConstructor = metadata.Constructors.Any(constructor => constructor.IsPublic && constructor.ParameterTypes.Count == 0);
            return true;
        }

        private static bool TryResolveTypeMemberString(
            SyntaxNode expression,
            string parameterName,
            ReflectionTypeMetadata metadata,
            out string value)
        {
            value = string.Empty;
            if (expression is ExpressionSyntax expressionSyntax)
            {
                expression = UnwrapExpression(expressionSyntax);
            }
            if (expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax identifier ||
                !string.Equals(identifier.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(memberAccess.Name.Identifier.ValueText, "Name", StringComparison.Ordinal))
            {
                value = metadata.Name;
                return true;
            }

            return false;
        }

        private static bool TryResolveStringLiteral(SyntaxNode expression, out string value)
        {
            if (expression is ExpressionSyntax expressionSyntax)
            {
                expression = UnwrapExpression(expressionSyntax);
            }
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                value = literal.Token.ValueText;
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool IsNullLiteral(SyntaxNode expression)
        {
            if (expression is ExpressionSyntax expressionSyntax)
            {
                expression = UnwrapExpression(expressionSyntax);
            }

            return expression is LiteralExpressionSyntax literal &&
                   literal.IsKind(SyntaxKind.NullLiteralExpression);
        }

        private static bool TryEvaluateAssignableFromPredicate(
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            string parameterName,
            ReflectionTypeMetadata metadata,
            out bool isAssignable)
        {
            isAssignable = false;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                !string.Equals(memberAccess.Name.Identifier.ValueText, "IsAssignableFrom", StringComparison.Ordinal) ||
                invocation.ArgumentList.Arguments.Count != 1)
            {
                return false;
            }

            if (invocation.ArgumentList.Arguments[0].Expression is not IdentifierNameSyntax identifier ||
                !string.Equals(identifier.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                return false;
            }

            if (memberAccess.Expression is not TypeOfExpressionSyntax typeOfExpression)
            {
                return false;
            }

            var baseType = ResolveEffectiveTypeSymbol(semanticModel, invocation, semanticModel.GetTypeInfo(typeOfExpression.Type).Type);
            if (baseType == null)
            {
                return false;
            }

            isAssignable = metadata.IsAssignableTo(baseType.OriginalDefinition.ToDisplayString());
            return true;
        }

        private static bool IsConstructorInfoInvoke(IMethodSymbol? calledMethod)
        {
            if (calledMethod != null)
            {
                var containingType = calledMethod.ContainingType?.ToDisplayString() ?? "";
                if (string.Equals(calledMethod.Name, "Invoke", StringComparison.Ordinal) &&
                    (string.Equals(containingType, "System.Reflection.ConstructorInfo", StringComparison.Ordinal) ||
                     string.Equals(containingType, "System.Reflection.MethodBase", StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksLikeConstructorInvokeSyntax(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   string.Equals(memberAccess.Name.Identifier.ValueText, "Invoke", StringComparison.Ordinal) &&
                   memberAccess.Expression is IdentifierNameSyntax;
        }

        private static bool LooksLikeGetConstructorSyntax(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   string.Equals(memberAccess.Name.Identifier.ValueText, "GetConstructor", StringComparison.Ordinal);
        }

        private static bool LooksLikeActivatorCreateInstanceSyntax(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   memberAccess.Expression is IdentifierNameSyntax identifier &&
                   string.Equals(identifier.Identifier.ValueText, "Activator", StringComparison.Ordinal) &&
                   string.Equals(memberAccess.Name.Identifier.ValueText, "CreateInstance", StringComparison.Ordinal);
        }

        private static bool IsActivatorCreateInstance(IMethodSymbol? calledMethod)
        {
            return calledMethod != null &&
                   string.Equals(calledMethod.Name, "CreateInstance", StringComparison.Ordinal) &&
                   string.Equals(calledMethod.ContainingType?.ToDisplayString(), "System.Activator", StringComparison.Ordinal);
        }



        internal static ExpressionSyntax UnwrapExpression(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
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
            if (!LooksLikeFrameworkOwnedSymbol(containingNamespace, containingType))
            {
                return false;
            }

            dispatchCaller = EnsureSyntheticFrameworkSymbol(
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




        private static IMethodSymbol? ResolveInvokedMethodSymbol(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
        {
            if (semanticModel.GetOperation(invocation) is IInvocationOperation invocationOperation)
            {
                return invocationOperation.TargetMethod;
            }

            return DirectCallExtractor.ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
        }

        private static IMethodSymbol? ResolveConstructedMethodSymbol(SemanticModel semanticModel, ObjectCreationExpressionSyntax creation)
        {
            if (semanticModel.GetOperation(creation) is IObjectCreationOperation creationOperation)
            {
                return creationOperation.Constructor;
            }

            return DirectCallExtractor.ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(creation));
        }

        private static IMethodSymbol? GetContainingExecutableMethod(ISymbol? symbol)
        {
            var current = symbol;
            while (current != null)
            {
                if (current is IMethodSymbol methodSymbol &&
                    methodSymbol.MethodKind != MethodKind.AnonymousFunction)
                {
                    return methodSymbol;
                }

                current = current.ContainingSymbol;
            }

            return null;
        }

        private static void PromoteAnonymousFunctionCalls(IMethodSymbol enclosingSymbol, SymbolData lambdaSymbol, ExtractionResult result)
        {
            var enclosingFqn = enclosingSymbol.OriginalDefinition.ToDisplayString();
            var promotedCalls = result.MethodCalls
                .Where(call => call.CallerId == lambdaSymbol.Fqn)
                .Select(call => new MethodCallData
                {
                    CallerId = enclosingFqn,
                    CalleeId = call.CalleeId,
                    CallCount = call.CallCount,
                    CallType = call.CallType == "calls" ? StructuralEdgeCatalog.LambdaDispatch : call.CallType
                })
                .ToList();

            if (promotedCalls.Count > 0)
            {
                result.MethodCalls.AddRange(promotedCalls);
            }
        }

        private CompilationAnalysisCache BuildCompilationCache(Compilation compilation)
        {
            var dispatchMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var methodLookup = new Dictionary<string, List<MethodLookupEntry>>(StringComparer.Ordinal);
            var autofacModuleTypes = new HashSet<string>(StringComparer.Ordinal);
            var autofacModuleLoadMethods = new HashSet<string>(StringComparer.Ordinal);
            var reflectionTypes = new Dictionary<string, ReflectionTypeMetadata>(StringComparer.Ordinal);
            var serviceRegistrations = CloneRegistrationMap(_solutionServiceRegistrations);
            VisitNamespace(compilation.GlobalNamespace, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods, reflectionTypes);
            ServiceRegistrationCollector.CollectServiceRegistrations(compilation, serviceRegistrations);
            return new CompilationAnalysisCache(dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods, reflectionTypes, serviceRegistrations);
        }

        private static Dictionary<string, HashSet<string>> CloneRegistrationMap(Dictionary<string, HashSet<string>> source)
        {
            return source.ToDictionary(
                entry => entry.Key,
                entry => new HashSet<string>(entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }



        private void VisitNamespace(INamespaceSymbol ns, Dictionary<string, HashSet<string>> dispatchMap, Dictionary<string, List<MethodLookupEntry>> methodLookup, HashSet<string> autofacModuleTypes, HashSet<string> autofacModuleLoadMethods, Dictionary<string, ReflectionTypeMetadata> reflectionTypes)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNs)
                {
                    VisitNamespace(childNs, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods, reflectionTypes);
                }
                else if (member is INamedTypeSymbol namedType)
                {
                    VisitType(namedType, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods, reflectionTypes);
                }
            }
        }

        private void VisitType(INamedTypeSymbol type, Dictionary<string, HashSet<string>> dispatchMap, Dictionary<string, List<MethodLookupEntry>> methodLookup, HashSet<string> autofacModuleTypes, HashSet<string> autofacModuleLoadMethods, Dictionary<string, ReflectionTypeMetadata> reflectionTypes)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                VisitType(nested, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods, reflectionTypes);
            }

            var typeFqn = type.OriginalDefinition.ToDisplayString();
            reflectionTypes[typeFqn] = CreateReflectionTypeMetadata(type);

            var isAutofacModule = IsOrDerivedFrom(type, "Autofac.Module");
            if (isAutofacModule)
            {
                autofacModuleTypes.Add(typeFqn);
            }

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                var lookupKey = BuildMethodLookupKey(type.OriginalDefinition.ToDisplayString(), method.Name);
                if (!methodLookup.TryGetValue(lookupKey, out var methods))
                {
                    methods = new List<MethodLookupEntry>();
                    methodLookup[lookupKey] = methods;
                }

                var fqn = method.OriginalDefinition.ToDisplayString();
                if (!methods.Any(m => m.Fqn == fqn))
                {
                    methods.Add(new MethodLookupEntry
                    {
                        Fqn = fqn,
                        ParameterCount = method.Parameters.Length
                    });
                }

                if (isAutofacModule &&
                    string.Equals(method.Name, "Load", StringComparison.Ordinal) &&
                    method.Parameters.Length == 1)
                {
                    autofacModuleLoadMethods.Add(fqn);
                }

                if (method.OverriddenMethod == null)
                {
                    continue;
                }

                var baseFqn = method.OverriddenMethod.OriginalDefinition.ToDisplayString();
                ServiceRegistrationCollector.AddDispatchTarget(dispatchMap, baseFqn, fqn);
            }

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var interfaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var implementation = type.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol;
                    if (implementation == null)
                    {
                        continue;
                    }

                    ServiceRegistrationCollector.AddDispatchTarget(dispatchMap, interfaceMethod.OriginalDefinition.ToDisplayString(), implementation.OriginalDefinition.ToDisplayString());
                }
            }
        }

        private static ReflectionTypeMetadata CreateReflectionTypeMetadata(INamedTypeSymbol type)
        {
            var assignableTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                type.OriginalDefinition.ToDisplayString()
            };

            var currentBase = type.BaseType;
            while (currentBase != null)
            {
                assignableTypes.Add(currentBase.OriginalDefinition.ToDisplayString());
                currentBase = currentBase.BaseType;
            }

            foreach (var iface in type.AllInterfaces)
            {
                assignableTypes.Add(iface.OriginalDefinition.ToDisplayString());
            }

            var constructors = type.InstanceConstructors
                .Where(ctor => !ctor.IsStatic && (ctor.DeclaredAccessibility == Accessibility.Public || !ctor.IsImplicitlyDeclared))
                .Select(ctor => new ReflectionConstructorMetadata(
                    ctor.OriginalDefinition.ToDisplayString(),
                    ctor.Parameters.Select(parameter => parameter.Type.OriginalDefinition.ToDisplayString()).ToArray(),
                    ctor.DeclaredAccessibility == Accessibility.Public))
                .ToList();

            return new ReflectionTypeMetadata(
                type.OriginalDefinition.ToDisplayString(),
                type.Name,
                type.IsAbstract,
                type.TypeKind == TypeKind.Class,
                type.TypeKind == TypeKind.Interface,
                assignableTypes,
                constructors);
        }



        private static string BuildMethodLookupKey(string containingType, string methodName)
        {
            return $"{containingType}|{methodName}";
        }

        private static string GetStableHash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }


        private static bool IsPartialDeclaration(SyntaxNode node)
        {
            return node switch
            {
                BaseTypeDeclarationSyntax typeDecl => typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
                MethodDeclarationSyntax methodDecl => methodDecl.Modifiers.Any(SyntaxKind.PartialKeyword),
                _ => false
            };
        }
    }








}
