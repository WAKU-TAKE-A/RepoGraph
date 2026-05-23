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
            var symbolData = MapToData(symbol, declarationNode);
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
                RecordTypeDependency(field.Type, symbolData.Fqn, result);
                ExtractDelegateReferences(semanticModel, declarationNode, symbolData, result);
            }
            else if (symbol is IPropertySymbol prop)
            {
                RecordTypeDependency(prop.Type, symbolData.Fqn, result);
            }
            else if (symbol is IMethodSymbol method)
            {
                RecordTypeDependency(method.ReturnType, symbolData.Fqn, result);
                foreach (var param in method.Parameters)
                {
                    RecordTypeDependency(param.Type, symbolData.Fqn, result);
                }

                ExtractOverrideDispatch(compilationCache, method, symbolData, result);
                ExtractLifecycleEntrypoints(method, symbolData, result);
                ExtractSerializationConventionEntrypoints(method, declarationNode, symbolData, result);
            }

            if ((symbol is IMethodSymbol && IsExecutableNode(declarationNode))
                || (symbol is IPropertySymbol && HasPropertyExecutableBody(declarationNode)))
            {
                ExtractMethodCalls(compilationCache, semanticModel, declarationNode, symbolData, result);
                ExtractFrameworkConventionDependencies(compilationCache, semanticModel, declarationNode, symbolData, result);
                ExtractThreadBoundaries(semanticModel, declarationNode, symbolData);
                ExtractFieldAccesses(semanticModel, declarationNode, symbolData, result);
                ExtractEventSubscriptions(semanticModel, declarationNode, symbolData, result);
                ExtractDelegateReferences(semanticModel, declarationNode, symbolData, result);
                ExtractTypeDependencies(semanticModel, declarationNode, symbolData, result);
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

            var symbolData = MapToData(constructorSymbol, typeDeclaration);
            if (result.Symbols.Any(existing => existing.Fqn == symbolData.Fqn))
            {
                return;
            }

            result.Symbols.Add(symbolData);
            foreach (var parameter in constructorSymbol.Parameters)
            {
                RecordTypeDependency(parameter.Type, symbolData.Fqn, result);
            }
        }

        private void ProcessAnonymousFunction(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, AnonymousFunctionExpressionSyntax anonymousFunction, ExtractionResult result)
        {
            var enclosingSymbol = GetContainingExecutableMethod(semanticModel.GetEnclosingSymbol(anonymousFunction.SpanStart));
            if (enclosingSymbol == null)
            {
                return;
            }

            var symbolData = CreateAnonymousFunctionData(enclosingSymbol, anonymousFunction);
            result.Symbols.Add(symbolData);

            ExtractMethodCalls(compilationCache, semanticModel, anonymousFunction, symbolData, result);
            ExtractFrameworkConventionDependencies(compilationCache, semanticModel, anonymousFunction, symbolData, result);
            ExtractThreadBoundaries(semanticModel, anonymousFunction, symbolData);
            ExtractFieldAccesses(semanticModel, anonymousFunction, symbolData, result);
            ExtractEventSubscriptions(semanticModel, anonymousFunction, symbolData, result);
            ExtractDelegateReferences(semanticModel, anonymousFunction, symbolData, result);
            ExtractTypeDependencies(semanticModel, anonymousFunction, symbolData, result);
            PromoteAnonymousFunctionCalls(enclosingSymbol, symbolData, result);
        }

        private void RecordTypeDependency(ITypeSymbol? type, string sourceFqn, ExtractionResult result)
        {
            if (type == null) return;
            
            var targetFqn = type.OriginalDefinition.ToDisplayString();
            if (targetFqn == "object" || targetFqn.StartsWith("System.")) return;

            result.TypeDependencies.Add(new TypeDependencyData
            {
                SourceFqn = sourceFqn,
                TargetFqn = targetFqn,
                Kind = StructuralEdgeCatalog.TypeUsage
            });

            if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                foreach (var arg in namedType.TypeArguments)
                {
                    RecordTypeDependency(arg, sourceFqn, result);
                }
            }
        }

        private void ExtractTypeDependencies(SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var typeNode in GetAnalysisDescendantNodes(node).OfType<TypeSyntax>())
            {
                var typeInfo = semanticModel.GetTypeInfo(typeNode);
                RecordTypeDependency(typeInfo.Type, symbolData.Fqn, result);
            }

            // Also check for 'new', 'as', 'is', and casts
            foreach (var creation in GetAnalysisDescendantNodes(node).OfType<ObjectCreationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(creation);
                if (symbolInfo.Symbol is IMethodSymbol ctor)
                {
                    RecordTypeDependency(ctor.ContainingType, symbolData.Fqn, result);
                }
                else
                {
                    var createdType = semanticModel.GetTypeInfo(creation).Type
                        ?? semanticModel.GetTypeInfo(creation.Type).Type;
                    RecordTypeDependency(createdType, symbolData.Fqn, result);
                }
            }

            foreach (var implicitCreation in GetAnalysisDescendantNodes(node).OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                var createdType = semanticModel.GetTypeInfo(implicitCreation).Type;
                RecordTypeDependency(createdType, symbolData.Fqn, result);
            }

            foreach (var cast in GetAnalysisDescendantNodes(node).OfType<CastExpressionSyntax>())
            {
                var typeInfo = semanticModel.GetTypeInfo(cast.Type);
                RecordTypeDependency(typeInfo.Type, symbolData.Fqn, result);
            }

            foreach (var binary in GetAnalysisDescendantNodes(node).OfType<BinaryExpressionSyntax>())
            {
                if (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression))
                {
                    var typeInfo = semanticModel.GetTypeInfo(binary.Right);
                    RecordTypeDependency(typeInfo.Type, symbolData.Fqn, result);
                }
            }
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
                result.Symbols.Add(CreateFrameworkMethodSymbol(method.OverriddenMethod));
            }

            result.MethodCalls.Add(new MethodCallData
            {
                CallerId = baseFqn,
                CalleeId = symbolData.Fqn,
                CallCount = 1,
                CallType = StructuralEdgeCatalog.OverrideDispatch
            });
        }

        private static SymbolData CreateFrameworkMethodSymbol(IMethodSymbol method)
        {
            var fqn = method.OriginalDefinition.ToDisplayString();
            return new SymbolData
            {
                Id = GetStableHash(fqn),
                Fqn = fqn,
                Name = method.Name,
                Kind = SyntheticSymbolKindCatalog.FrameworkMethod,
                Namespace = method.ContainingNamespace?.ToDisplayString(),
                ContainingType = method.ContainingType?.ToDisplayString(),
                Accessibility = method.DeclaredAccessibility.ToString().ToLower(),
                IsStatic = method.IsStatic,
                IsAbstract = method.IsAbstract,
                IsSealed = method.IsSealed,
                IsAsync = method.IsAsync || method.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task"),
                IsPartial = false,
                IsGeneric = method.IsGenericMethod,
                IsExtensionMethod = method.IsExtensionMethod,
                IsDisposable = false,
                IsVolatile = false,
                LineStart = 0,
                LineEnd = 0,
                Loc = 0,
                ParameterCount = method.Parameters.Length,
                ReturnType = method.ReturnType.ToDisplayString(),
                HasCallback = method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate || p.Type.Name.StartsWith("Action") || p.Type.Name.StartsWith("Func"))
            };
        }

        /// <summary>
        /// Extract method invocation calls.
        /// </summary>
        private void ExtractMethodCalls(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var invocations = GetAnalysisDescendantNodes(methodNode).OfType<InvocationExpressionSyntax>();
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

        /// <summary>
        /// Detect thread boundary patterns in method body:
        /// - Invoke/BeginInvoke (UI thread dispatch)
        /// - Task.Run / Task.Factory.StartNew (background thread spawn)
        /// - BackgroundWorker usage
        /// - Application.DoEvents() (re-entrancy hazard)
        /// - lock statements (mutual exclusion)
        /// </summary>
        private void ExtractThreadBoundaries(SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData)
        {
            // Check for lock statements
            symbolData.HasLock = GetAnalysisDescendantNodes(methodNode).OfType<LockStatementSyntax>().Any();

            var invocations = GetAnalysisDescendantNodes(methodNode).OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var calledMethod = ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
                if (calledMethod != null)
                {
                    var methodName = calledMethod.Name;
                    var containingTypeFqn = calledMethod.ContainingType?.ToDisplayString() ?? "";

                    // Invoke / BeginInvoke on WinForms controls or WPF dispatcher.
                    if ((methodName == "Invoke" || methodName == "BeginInvoke") &&
                        (IsOrDerivedFrom(calledMethod.ContainingType, "System.Windows.Forms.Control") ||
                         containingTypeFqn == "System.Windows.Threading.Dispatcher"))
                    {
                        symbolData.HasUiDispatch = true;
                    }

                    if ((methodName == "InvokeAsync" || methodName == "BeginInvoke") &&
                        containingTypeFqn == "System.Windows.Threading.Dispatcher")
                    {
                        symbolData.HasUiDispatch = true;
                    }

                    // Task.Run / Task.Factory.StartNew (background thread spawn)
                    var typeFqn = calledMethod.ContainingType?.ToDisplayString() ?? "";
                    if ((methodName == "Run" && typeFqn.StartsWith("System.Threading.Tasks.Task")) ||
                        (methodName == "StartNew" && typeFqn.StartsWith("System.Threading.Tasks.TaskFactory")))
                    {
                        symbolData.HasTaskSpawn = true;
                    }

                    if ((methodName == "Post" || methodName == "Send") &&
                        IsOrDerivedFrom(calledMethod.ContainingType, "System.Threading.SynchronizationContext"))
                    {
                        symbolData.HasUiDispatch = true;
                    }

                    // BackgroundWorker.RunWorkerAsync
                    if (methodName == "RunWorkerAsync" &&
                        IsOrDerivedFrom(calledMethod.ContainingType, "System.ComponentModel.BackgroundWorker"))
                    {
                        symbolData.HasBackgroundWorker = true;
                    }

                    // Application.DoEvents()
                    if (methodName == "DoEvents" &&
                        typeFqn.StartsWith("System.Windows.Forms.Application"))
                    {
                        symbolData.HasDoEvents = true;
                    }

                    // Thread, ThreadPool, Parallel
                    if (methodName == "Start" && typeFqn == "System.Threading.Thread") symbolData.HasThreadStart = true;
                    if (methodName == "QueueUserWorkItem" && typeFqn == "System.Threading.ThreadPool") symbolData.HasThreadStart = true;
                    if ((methodName == "For" || methodName == "ForEach") && typeFqn == "System.Threading.Tasks.Parallel") symbolData.HasThreadStart = true;

                    // Blocking waits
                    if (methodName == "Wait" && typeFqn.StartsWith("System.Threading.Tasks.Task")) symbolData.HasBlockingWait = true;
                    if (methodName == "Join" && typeFqn == "System.Threading.Thread") symbolData.HasBlockingWait = true;
                }
                else
                {
                    // Fallback: If semantic resolution failed (e.g. missing SDK), check syntactically
                    var methodText = invocation.Expression.ToString();
                    if (methodText.Contains("Task.Run") || methodText.Contains("TaskFactory.StartNew") || (methodText.Contains("Task<") && methodText.Contains(".Run")))
                    {
                        symbolData.HasTaskSpawn = true;
                    }
                    if (methodText.Contains("Dispatcher.Invoke") || methodText.Contains("Dispatcher.BeginInvoke") ||
                        methodText.Contains("Control.Invoke") || methodText.Contains("Control.BeginInvoke") ||
                        methodText.Contains("SynchronizationContext.Post") || methodText.Contains("SynchronizationContext.Send"))
                    {
                        symbolData.HasUiDispatch = true;
                    }
                    if (methodText.Contains("Application.DoEvents"))
                    {
                        symbolData.HasDoEvents = true;
                    }
                    if (methodText.Contains("ThreadPool.QueueUserWorkItem") || methodText.Contains("Parallel.For"))
                    {
                        symbolData.HasThreadStart = true;
                    }
                    if (methodText.EndsWith(".Wait") || methodText.EndsWith(".Join"))
                    {
                        symbolData.HasBlockingWait = true;
                    }
                }
            }

            // Global text fallback for the whole method body to catch properties like .Result and object creations like new Thread
            var fullText = methodNode.ToString();
            if (fullText.Contains(".Result")) symbolData.HasBlockingWait = true;
            if (fullText.Contains("new Thread(")) symbolData.HasThreadStart = true;

            // Also check for BackgroundWorker field declarations used in the method (e.g., _bgw.IsBusy)
            var memberAccesses = GetAnalysisDescendantNodes(methodNode).OfType<MemberAccessExpressionSyntax>();
            foreach (var memberAccess in memberAccesses)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
                if (symbolInfo.Symbol is IFieldSymbol field &&
                    IsOrDerivedFrom(field.Type, "System.ComponentModel.BackgroundWorker"))
                {
                    symbolData.HasBackgroundWorker = true;
                    break;
                }
                if (symbolInfo.Symbol is ILocalSymbol local &&
                    IsOrDerivedFrom(local.Type, "System.ComponentModel.BackgroundWorker"))
                {
                    symbolData.HasBackgroundWorker = true;
                    break;
                }
            }
        }

        /// <summary>
        /// Extract all field and property accesses within a method body.
        /// Tracks read vs write, and whether the access is to an external class.
        /// </summary>
        private void ExtractFieldAccesses(SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var containingTypeFqn = symbolData.ContainingType;

            foreach (var identNode in GetAnalysisDescendantNodes(methodNode).OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identNode);
                var accessedSymbol = symbolInfo.Symbol;

                if (accessedSymbol is IFieldSymbol field)
                {
                    RecordFieldAccess(field, field.ContainingType, identNode, symbolData.Fqn, containingTypeFqn, result);
                }
                else if (accessedSymbol is IPropertySymbol prop)
                {
                    RecordFieldAccess(prop, prop.ContainingType, identNode, symbolData.Fqn, containingTypeFqn, result);
                }
            }
        }

        private void RecordFieldAccess(ISymbol accessedSymbol, INamedTypeSymbol? ownerType, IdentifierNameSyntax identNode, string accessorFqn, string? containingTypeFqn, ExtractionResult result)
        {
            var targetFqn = accessedSymbol.ToDisplayString();
            var ownerFqn = ownerType?.ToDisplayString() ?? "";

            // Determine read vs write: check if the identifier is on the left side of an assignment
            var accessKind = DetermineAccessKind(identNode);

            // Determine if external (different class)
            bool isExternal = !string.Equals(ownerFqn, containingTypeFqn, StringComparison.Ordinal);

            result.FieldAccesses.Add(new FieldAccessData
            {
                AccessorFqn = accessorFqn,
                TargetFqn = targetFqn,
                AccessKind = accessKind,
                IsExternal = isExternal
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

        /// <summary>
        /// Detect event handler subscriptions (+=) and unsubscriptions (-=).
        /// Records them as special method calls with type "event_subscribe" / "event_unsubscribe".
        /// </summary>
        private void ExtractEventSubscriptions(SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var assignments = GetAnalysisDescendantNodes(methodNode).OfType<AssignmentExpressionSyntax>();
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



        private static IEnumerable<SyntaxNode> GetAnalysisDescendantNodes(SyntaxNode node)
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
        private static bool IsOrDerivedFrom(ITypeSymbol? type, string baseTypeFqn)
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

        private static bool LooksLikeFrameworkOwnedSymbol(string symbolNamespace, string? containingType)
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

        private static void ExtractLifecycleEntrypoints(IMethodSymbol method, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var entrypoint in LifecycleConventionExtractor.GetEntrypoints(method))
            {
                var callerFqn = EnsureSyntheticFrameworkSymbol(
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

        private static void ExtractSerializationConventionEntrypoints(IMethodSymbol method, SyntaxNode declarationNode, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var entrypoint in SerializationConventionExtractor.GetEntrypoints(method, declarationNode))
            {
                var callerFqn = EnsureSyntheticFrameworkSymbol(
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

        private static string EnsureSyntheticFrameworkSymbol(
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

        private void ExtractFrameworkConventionDependencies(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var invocation in GetAnalysisDescendantNodes(node).OfType<InvocationExpressionSyntax>())
            {
                var calledMethod = ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
                if (TryExtractReflectionConstructorDispatch(compilationCache, semanticModel, node, invocation, calledMethod, symbolData, result))
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







        private static bool TryExtractReflectionConstructorDispatch(
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

        private void ExtractDelegateReferences(SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var invocation in GetAnalysisDescendantNodes(node).OfType<InvocationExpressionSyntax>())
            {
                var calledMethod = ResolveInvokedMethodSymbol(semanticModel, invocation);
                if (calledMethod == null)
                {
                    TryRecordFrameworkDelegateArgumentsFromInvocationFallback(semanticModel, invocation, symbolData, result);
                    continue;
                }

                RecordDelegateArguments(semanticModel, symbolData, result, calledMethod, calledMethod.Parameters, invocation.ArgumentList.Arguments);
            }

            foreach (var creation in GetAnalysisDescendantNodes(node).OfType<ObjectCreationExpressionSyntax>())
            {
                var constructor = ResolveConstructedMethodSymbol(semanticModel, creation);
                if (constructor == null)
                {
                    if (creation.ArgumentList != null)
                    {
                        TryRecordFrameworkDelegateArgumentsFromObjectCreationFallback(semanticModel, creation, symbolData, result);
                    }

                    continue;
                }

                if (creation.ArgumentList == null)
                {
                    continue;
                }

                RecordDelegateArguments(semanticModel, symbolData, result, constructor, constructor.Parameters, creation.ArgumentList.Arguments);
            }
        }

        private static void TryRecordFrameworkDelegateArgumentsFromInvocationFallback(
            SemanticModel semanticModel,
            InvocationExpressionSyntax invocation,
            SymbolData symbolData,
            ExtractionResult result)
        {
            if (!TryDescribeFrameworkDelegateInvocationFallback(semanticModel, invocation, out var descriptor))
            {
                return;
            }

            RecordFrameworkDelegateFallbackTargets(semanticModel, symbolData, result, invocation.ArgumentList.Arguments, descriptor);
        }

        private static void TryRecordFrameworkDelegateArgumentsFromObjectCreationFallback(
            SemanticModel semanticModel,
            ObjectCreationExpressionSyntax creation,
            SymbolData symbolData,
            ExtractionResult result)
        {
            if (creation.ArgumentList == null ||
                !TryDescribeFrameworkDelegateObjectCreationFallback(semanticModel, creation, out var descriptor))
            {
                return;
            }

            RecordFrameworkDelegateFallbackTargets(semanticModel, symbolData, result, creation.ArgumentList.Arguments, descriptor);
        }

        private static void RecordFrameworkDelegateFallbackTargets(
            SemanticModel semanticModel,
            SymbolData symbolData,
            ExtractionResult result,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            FrameworkDelegateFallbackDescriptor descriptor)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                var targets = DelegateTargetResolver.ResolveDelegateTargetMethods(semanticModel, arguments[i].Expression).ToList();
                if (targets.Count == 0)
                {
                    continue;
                }

                var fallbackCallerFqn = EnsureSyntheticFrameworkSymbol(
                    result,
                    $"{descriptor.FrameworkCallerPrefix}::arg{i}",
                    descriptor.FrameworkCallerName,
                    descriptor.FrameworkNamespace,
                    descriptor.FrameworkContainingType,
                    descriptor.ReturnType,
                    descriptor.ParameterCount);

                foreach (var targetMethod in targets)
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

                    result.MethodCalls.Add(new MethodCallData
                    {
                        CallerId = fallbackCallerFqn,
                        CalleeId = handlerFqn,
                        CallCount = 1,
                        CallType = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.CallType,
                        RuleId = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.RuleId,
                        RuleFamily = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.Family,
                        RuleMode = FrameworkRuleCatalog.FrameworkDelegateFallbackCandidate.ModeName
                    });
                }
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
                    LooksLikeFrameworkOwnedSymbol(
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
                        LooksLikeFrameworkOwnedSymbol(
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
                LooksLikeFrameworkOwnedSymbol(
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

        private static IEnumerable<string> ResolveFallbackMethodTargets(CompilationAnalysisCache compilationCache, SemanticModel semanticModel, InvocationExpressionSyntax invocation, SymbolData symbolData)
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

        private static SymbolData CreateAnonymousFunctionData(IMethodSymbol enclosingSymbol, AnonymousFunctionExpressionSyntax anonymousFunction)
        {
            var lineSpan = anonymousFunction.GetLocation().GetLineSpan();
            var line = lineSpan.StartLinePosition.Line + 1;
            var column = lineSpan.StartLinePosition.Character + 1;
            var name = $"<lambda@L{line}C{column}>";
            var fqn = $"{enclosingSymbol.OriginalDefinition.ToDisplayString()}.{name}";

            var parameterCount = anonymousFunction switch
            {
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
                SimpleLambdaExpressionSyntax => 1,
                AnonymousMethodExpressionSyntax anonymousMethod when anonymousMethod.ParameterList != null => anonymousMethod.ParameterList.Parameters.Count,
                _ => 0
            };

            return new SymbolData
            {
                Id = GetStableHash(fqn),
                Fqn = fqn,
                Name = name,
                Kind = SyntheticSymbolKindCatalog.Lambda,
                Namespace = enclosingSymbol.ContainingNamespace?.ToDisplayString(),
                ContainingType = enclosingSymbol.ContainingType?.ToDisplayString(),
                Accessibility = "private",
                IsStatic = enclosingSymbol.IsStatic,
                IsAsync = anonymousFunction.AsyncKeyword != default,
                LineStart = line,
                LineEnd = lineSpan.EndLinePosition.Line + 1,
                Loc = anonymousFunction.ToString().Split('\n').Length,
                ParameterCount = parameterCount,
                ReturnType = "unknown"
            };
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

        private static IMethodSymbol? ResolveInvokedMethodSymbol(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
        {
            if (semanticModel.GetOperation(invocation) is IInvocationOperation invocationOperation)
            {
                return invocationOperation.TargetMethod;
            }

            return ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
        }

        private static IMethodSymbol? ResolveConstructedMethodSymbol(SemanticModel semanticModel, ObjectCreationExpressionSyntax creation)
        {
            if (semanticModel.GetOperation(creation) is IObjectCreationOperation creationOperation)
            {
                return creationOperation.Constructor;
            }

            return ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(creation));
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

        private SymbolData MapToData(ISymbol symbol, SyntaxNode node)
        {
            var fqn = symbol.ToDisplayString();
            var data = new SymbolData
            {
                Id = GetStableHash(fqn), // Use stable hash for ID
                Fqn = fqn,
                Name = symbol.Name,
                Kind = symbol.Kind.ToString().ToLower(),
                Namespace = symbol.ContainingNamespace?.ToDisplayString(),
                ContainingType = symbol.ContainingType?.ToDisplayString(),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                IsStatic = symbol.IsStatic,
                IsAbstract = symbol.IsAbstract,
                IsSealed = symbol.IsSealed,
                IsAsync = false,
                IsPartial = IsPartialDeclaration(node),
                IsGeneric = false,
                IsVolatile = false,
                LineStart = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                LineEnd = node.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                Loc = node.ToString().Split('\n').Length
            };

            if (symbol is IMethodSymbol method)
            {
                data.IsAsync = method.IsAsync || method.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task");
                data.IsGeneric = method.IsGenericMethod;
                data.ParameterCount = method.Parameters.Length;
                data.ReturnType = method.ReturnType.ToDisplayString();
                data.IsExtensionMethod = method.IsExtensionMethod;
                if (method.MethodKind == MethodKind.Constructor)
                    data.Kind = "constructor";
                
                // Detect if method takes a callback/delegate to help identify "Callback Hell"
                data.HasCallback = method.Parameters.Any(p => p.Type.TypeKind == TypeKind.Delegate || p.Type.Name.StartsWith("Action") || p.Type.Name.StartsWith("Func"));
            }
            else if (symbol is INamedTypeSymbol type)
            {
                data.IsGeneric = type.IsGenericType;
                data.Kind = type.TypeKind.ToString().ToLower();
                data.IsDisposable = type.AllInterfaces.Any(i => i.ToDisplayString() == "System.IDisposable");
            }
            else if (symbol is IFieldSymbol fieldSymbol)
            {
                data.IsVolatile = fieldSymbol.IsVolatile;
            }

            return data;
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

    public class SymbolData
    {
        public string Id { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Fqn { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Namespace { get; set; }
        public string? ContainingType { get; set; }
        public string Accessibility { get; set; } = "";
        public bool IsStatic { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsAsync { get; set; }
        public bool IsPartial { get; set; }
        public bool IsGeneric { get; set; }
        public bool IsExtensionMethod { get; set; }
        public bool IsDisposable { get; set; }
        public bool IsVolatile { get; set; }
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public int Loc { get; set; }
        public int ParameterCount { get; set; }
        public string? ReturnType { get; set; }
        public bool HasCallback { get; set; }
        // Thread boundary flags
        public bool HasUiDispatch { get; set; }
        public bool HasTaskSpawn { get; set; }
        public bool HasBackgroundWorker { get; set; }
        public bool HasDoEvents { get; set; }
        public bool HasLock { get; set; }
        public bool HasThreadStart { get; set; }
        public bool HasBlockingWait { get; set; }
        public int FanIn { get; set; }
    }

    public class MethodCallData
    {
        public string CallerId { get; set; } = "";
        public string CalleeId { get; set; } = "";
        public int CallCount { get; set; }
        public string CallType { get; set; } = "calls"; // "calls", "event_subscribe", "event_unsubscribe"
        public string? RuleId { get; set; }
        public string? RuleFamily { get; set; }
        public string? RuleMode { get; set; }
    }

    public class FieldAccessData
    {
        public string AccessorFqn { get; set; } = "";
        public string TargetFqn { get; set; } = "";
        public string AccessKind { get; set; } = "read"; // "read", "write", "read_write"
        public bool IsExternal { get; set; }
    }

    public class ProjectDependencyData
    {
        public string SourceProjectId { get; set; } = "";
        public string TargetProjectId { get; set; } = "";
    }

    public class InheritanceData
    {
        public string DerivedId { get; set; } = "";
        public string BaseId { get; set; } = "";
        public string Kind { get; set; } = "";
    }

    public class TypeDependencyData
    {
        public string SourceFqn { get; set; } = "";
        public string TargetFqn { get; set; } = "";
        public string Kind { get; set; } = "type_usage";
        public string? RuleId { get; set; }
        public string? RuleFamily { get; set; }
        public string? RuleMode { get; set; }
    }

    public class ExtractionResult
    {
        public List<SymbolData> Symbols { get; set; } = new List<SymbolData>();
        public List<MethodCallData> MethodCalls { get; set; } = new List<MethodCallData>();
        public List<InheritanceData> Inheritances { get; set; } = new List<InheritanceData>();
        public List<FieldAccessData> FieldAccesses { get; set; } = new List<FieldAccessData>();
        public List<TypeDependencyData> TypeDependencies { get; set; } = new List<TypeDependencyData>();
    }

    internal sealed class CompilationAnalysisCache
    {
        private readonly Dictionary<string, HashSet<string>> _dispatchMap;
        private readonly Dictionary<string, List<MethodLookupEntry>> _methodLookup;
        private readonly HashSet<string> _knownMethods;
        private readonly HashSet<string> _autofacModuleTypes;
        private readonly HashSet<string> _autofacModuleLoadMethods;
        private readonly Dictionary<string, ReflectionTypeMetadata> _reflectionTypes;
        private readonly Dictionary<string, HashSet<string>> _serviceRegistrations;

        public CompilationAnalysisCache(
            Dictionary<string, HashSet<string>> dispatchMap,
            Dictionary<string, List<MethodLookupEntry>> methodLookup,
            HashSet<string> autofacModuleTypes,
            HashSet<string> autofacModuleLoadMethods,
            Dictionary<string, ReflectionTypeMetadata> reflectionTypes,
            Dictionary<string, HashSet<string>> serviceRegistrations)
        {
            _dispatchMap = dispatchMap;
            _methodLookup = methodLookup;
            _knownMethods = methodLookup.Values
                .SelectMany(methods => methods)
                .Select(method => method.Fqn)
                .ToHashSet(StringComparer.Ordinal);
            _autofacModuleTypes = autofacModuleTypes;
            _autofacModuleLoadMethods = autofacModuleLoadMethods;
            _reflectionTypes = reflectionTypes;
            _serviceRegistrations = serviceRegistrations;
        }

        public IEnumerable<string> GetDispatchTargets(IMethodSymbol calledMethod)
        {
            var baseFqn = calledMethod.OriginalDefinition.ToDisplayString();
            return _dispatchMap.TryGetValue(baseFqn, out var targets)
                ? targets
                : Array.Empty<string>();
        }

        public IEnumerable<string> GetMethodCandidates(string containingType, string methodName, int argumentCount)
        {
            var key = $"{containingType}|{methodName}";
            if (!_methodLookup.TryGetValue(key, out var methods))
            {
                return Array.Empty<string>();
            }

            var exact = methods
                .Where(m => m.ParameterCount == argumentCount)
                .Select(m => m.Fqn)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (exact.Count > 0)
            {
                return exact;
            }

            return methods.Select(m => m.Fqn).Distinct(StringComparer.Ordinal).ToList();
        }

        public bool KnowsMethod(string fqn)
        {
            return _knownMethods.Contains(fqn);
        }

        public IEnumerable<string> GetAutofacModuleTypes()
        {
            return _autofacModuleTypes;
        }

        public IEnumerable<string> GetAutofacModuleLoadMethods()
        {
            return _autofacModuleLoadMethods;
        }

        public HashSet<string> GetAllTypeFqns()
        {
            return _reflectionTypes.Keys.ToHashSet(StringComparer.Ordinal);
        }

        public HashSet<string> GetConcreteTypesAssignableTo(string baseTypeFqn)
        {
            return _reflectionTypes.Values
                .Where(type => !type.IsAbstract && !type.IsInterface && type.IsAssignableTo(baseTypeFqn))
                .Select(type => type.Fqn)
                .ToHashSet(StringComparer.Ordinal);
        }

        public bool TryGetTypeMetadata(string fqn, out ReflectionTypeMetadata metadata)
        {
            return _reflectionTypes.TryGetValue(fqn, out metadata!);
        }

        public IEnumerable<string> ResolveServiceDispatchConstructors(string requestedTypeFqn)
        {
            List<string> candidateTypes;
            if (_serviceRegistrations.TryGetValue(requestedTypeFqn, out var registeredImplementations))
            {
                candidateTypes = registeredImplementations
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                candidateTypes = new List<string>();
                if (_reflectionTypes.TryGetValue(requestedTypeFqn, out var requestedTypeMetadata) &&
                    !requestedTypeMetadata.IsAbstract &&
                    !requestedTypeMetadata.IsInterface)
                {
                    candidateTypes.Add(requestedTypeFqn);
                }
            }

            candidateTypes = candidateTypes
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (candidateTypes.Count != 1)
            {
                yield break;
            }

            if (!_reflectionTypes.TryGetValue(candidateTypes[0], out var resolvedType))
            {
                yield break;
            }

            var publicConstructors = resolvedType.Constructors
                .Where(constructor => constructor.IsPublic)
                .ToList();
            if (publicConstructors.Count != 1)
            {
                yield break;
            }

            yield return publicConstructors[0].Fqn;
        }

        public IEnumerable<string> GetConstructorCandidates(IEnumerable<string> candidateTypeFqns, IReadOnlyList<string> parameterTypes)
        {
            foreach (var typeFqn in candidateTypeFqns)
            {
                if (!_reflectionTypes.TryGetValue(typeFqn, out var metadata))
                {
                    continue;
                }

                foreach (var constructor in metadata.Constructors)
                {
                    if (constructor.ParameterTypes.Count != parameterTypes.Count)
                    {
                        continue;
                    }

                    var exactMatch = true;
                    for (var i = 0; i < parameterTypes.Count; i++)
                    {
                        if (!string.Equals(constructor.ParameterTypes[i], parameterTypes[i], StringComparison.Ordinal))
                        {
                            exactMatch = false;
                            break;
                        }
                    }

                    if (exactMatch)
                    {
                        yield return constructor.Fqn;
                    }
                }
            }
        }
    }

    internal sealed class MethodLookupEntry
    {
        public string Fqn { get; set; } = "";
        public int ParameterCount { get; set; }
    }

    internal sealed record ReflectionTypeMetadata(
        string Fqn,
        string Name,
        bool IsAbstract,
        bool IsClass,
        bool IsInterface,
        IReadOnlySet<string> AssignableTypeFqns,
        IReadOnlyList<ReflectionConstructorMetadata> Constructors)
    {
        public bool IsAssignableTo(string baseTypeFqn)
        {
            return AssignableTypeFqns.Contains(baseTypeFqn);
        }
    }

    internal sealed record ReflectionConstructorMetadata(
        string Fqn,
        IReadOnlyList<string> ParameterTypes,
        bool IsPublic);

    internal sealed record FrameworkEntrypoint(
        FrameworkRuleMetadata Rule,
        string FrameworkCallerFqn,
        string FrameworkCallerName,
        string FrameworkNamespace,
        string FrameworkContainingType);
}
