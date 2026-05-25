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

        public SymbolExtractor(ILogger<SymbolExtractor> logger)
        {
            _logger = logger;
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
                
            }

            if ((symbol is IMethodSymbol && IsExecutableNode(declarationNode))
                || (symbol is IPropertySymbol && HasPropertyExecutableBody(declarationNode)))
            {
                DirectCallExtractor.ExtractMethodCalls(compilationCache, semanticModel, declarationNode, symbolData, result);
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

        private static void AddDispatchTarget(Dictionary<string, HashSet<string>> dispatchMap, string contractFqn, string implementationFqn)
        {
            if (!dispatchMap.TryGetValue(contractFqn, out var targets))
            {
                targets = new HashSet<string>(StringComparer.Ordinal);
                dispatchMap[contractFqn] = targets;
            }

            targets.Add(implementationFqn);
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
            var reflectionTypes = new Dictionary<string, ReflectionTypeMetadata>(StringComparer.Ordinal);
            VisitNamespace(compilation.GlobalNamespace, dispatchMap, methodLookup, reflectionTypes);
            return new CompilationAnalysisCache(dispatchMap, methodLookup, reflectionTypes);
        }



        private void VisitNamespace(INamespaceSymbol ns, Dictionary<string, HashSet<string>> dispatchMap, Dictionary<string, List<MethodLookupEntry>> methodLookup, Dictionary<string, ReflectionTypeMetadata> reflectionTypes)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNs)
                {
                    VisitNamespace(childNs, dispatchMap, methodLookup, reflectionTypes);
                }
                else if (member is INamedTypeSymbol namedType)
                {
                    VisitType(namedType, dispatchMap, methodLookup, reflectionTypes);
                }
            }
        }

        private void VisitType(INamedTypeSymbol type, Dictionary<string, HashSet<string>> dispatchMap, Dictionary<string, List<MethodLookupEntry>> methodLookup, Dictionary<string, ReflectionTypeMetadata> reflectionTypes)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                VisitType(nested, dispatchMap, methodLookup, reflectionTypes);
            }

            var typeFqn = type.OriginalDefinition.ToDisplayString();
            reflectionTypes[typeFqn] = CreateReflectionTypeMetadata(type);

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

                if (method.OverriddenMethod == null)
                {
                    continue;
                }

                var baseFqn = method.OverriddenMethod.OriginalDefinition.ToDisplayString();
                AddDispatchTarget(dispatchMap, baseFqn, fqn);
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

                    AddDispatchTarget(dispatchMap, interfaceMethod.OriginalDefinition.ToDisplayString(), implementation.OriginalDefinition.ToDisplayString());
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
