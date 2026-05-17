using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
                }
            }

            foreach (var anonymousFunction in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
            {
                ProcessAnonymousFunction(compilationCache, semanticModel, anonymousFunction, result);
            }

            // Aggregate call counts
            result.MethodCalls = result.MethodCalls
                .GroupBy(c => new { c.CallerId, c.CalleeId, c.CallType })
                .Select(g => new MethodCallData
                {
                    CallerId = g.Key.CallerId,
                    CalleeId = g.Key.CalleeId,
                    CallCount = g.Sum(x => x.CallCount),
                    CallType = g.Key.CallType
                }).ToList();

            // Deduplicate field accesses (upgrade read to read_write if both read and write exist)
            result.FieldAccesses = DeduplicateFieldAccesses(result.FieldAccesses);
            result.TypeDependencies = result.TypeDependencies
                .GroupBy(d => new { d.SourceFqn, d.TargetFqn, d.Kind })
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
                        Kind = "extends"
                    });
                }

                foreach (var iface in namedType.Interfaces)
                {
                    result.Inheritances.Add(new InheritanceData
                    {
                        DerivedId = symbolData.Fqn,
                        BaseId = iface.OriginalDefinition.ToDisplayString(),
                        Kind = "implements"
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
                Kind = "type_usage"
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
                CallType = "override_dispatch"
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
                Kind = "framework_method",
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
                                CallType = "dynamic_dispatch"
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
                            CallType = "calls_fallback"
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
                    var leftInfo = semanticModel.GetSymbolInfo(assignment.Left);
                    if (leftInfo.Symbol is IEventSymbol eventSymbol)
                    {
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

                    foreach (var handlerMethod in ResolveDelegateTargetMethods(semanticModel, assignment.Right))
                    {
                        result.MethodCalls.Add(new MethodCallData
                        {
                            CallerId = symbolData.Fqn,
                            CalleeId = handlerMethod.OriginalDefinition.ToDisplayString(),
                            CallCount = 1,
                            CallType = "delegate_reference"
                        });
                    }
                }
            }
        }

        private static IEnumerable<IMethodSymbol> ResolveDelegateTargetMethods(SemanticModel semanticModel, ExpressionSyntax expression)
        {
            var directInfo = semanticModel.GetSymbolInfo(expression);
            if (directInfo.Symbol is IMethodSymbol directMethod)
            {
                yield return directMethod;
                yield break;
            }

            if (expression is ObjectCreationExpressionSyntax creation &&
                creation.ArgumentList is { Arguments.Count: > 0 })
            {
                foreach (var argument in creation.ArgumentList.Arguments)
                {
                    var argInfo = semanticModel.GetSymbolInfo(argument.Expression);
                    if (argInfo.Symbol is IMethodSymbol method)
                    {
                        yield return method;
                    }
                }
            }
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
                if (current.ToDisplayString() == baseTypeFqn)
                    return true;
                current = current.BaseType;
            }
            return false;
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
                if (calledMethod == null)
                {
                    continue;
                }

                if (TryExtractMvvmToolkitMessagingDispatch(compilationCache, calledMethod, symbolData, result))
                {
                    continue;
                }

                if (TryExtractAutofacModuleDispatch(compilationCache, calledMethod, symbolData, result))
                {
                    continue;
                }
            }
        }

        private static bool TryExtractMvvmToolkitMessagingDispatch(
            CompilationAnalysisCache compilationCache,
            IMethodSymbol calledMethod,
            SymbolData symbolData,
            ExtractionResult result)
        {
            if (!IsMvvmToolkitRegisterAll(calledMethod))
            {
                return false;
            }

            foreach (var receiveMethod in compilationCache.GetMethodCandidates(symbolData.ContainingType ?? "", "Receive", 1))
            {
                result.MethodCalls.Add(new MethodCallData
                {
                    CallerId = symbolData.Fqn,
                    CalleeId = receiveMethod,
                    CallCount = 1,
                    CallType = "mvvm_toolkit_message_dispatch"
                });
            }

            return true;
        }

        private static bool TryExtractAutofacModuleDispatch(
            CompilationAnalysisCache compilationCache,
            IMethodSymbol calledMethod,
            SymbolData symbolData,
            ExtractionResult result)
        {
            if (!IsAutofacRegisterAssemblyModules(calledMethod))
            {
                return false;
            }

            foreach (var moduleType in compilationCache.GetAutofacModuleTypes())
            {
                result.TypeDependencies.Add(new TypeDependencyData
                {
                    SourceFqn = symbolData.Fqn,
                    TargetFqn = moduleType,
                    Kind = "autofac_reflection_registration"
                });
            }

            foreach (var loadMethod in compilationCache.GetAutofacModuleLoadMethods())
            {
                result.MethodCalls.Add(new MethodCallData
                {
                    CallerId = symbolData.Fqn,
                    CalleeId = loadMethod,
                    CallCount = 1,
                    CallType = "autofac_module_load"
                });
            }

            return true;
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

        private static bool IsAutofacRegisterAssemblyModules(IMethodSymbol calledMethod)
        {
            var containingNamespace = calledMethod.ContainingNamespace?.ToDisplayString() ?? "";
            return string.Equals(calledMethod.Name, "RegisterAssemblyModules", StringComparison.Ordinal)
                && containingNamespace.Contains("Autofac", StringComparison.Ordinal);
        }

        private void ExtractDelegateReferences(SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var invocation in GetAnalysisDescendantNodes(node).OfType<InvocationExpressionSyntax>())
            {
                var calledMethod = ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
                if (calledMethod == null)
                {
                    continue;
                }

                RecordDelegateArguments(semanticModel, symbolData, result, calledMethod.Parameters, invocation.ArgumentList.Arguments);
            }

            foreach (var creation in GetAnalysisDescendantNodes(node).OfType<ObjectCreationExpressionSyntax>())
            {
                var constructor = ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(creation));
                if (constructor == null || creation.ArgumentList == null)
                {
                    continue;
                }

                RecordDelegateArguments(semanticModel, symbolData, result, constructor.Parameters, creation.ArgumentList.Arguments);
            }
        }

        private static void RecordDelegateArguments(
            SemanticModel semanticModel,
            SymbolData symbolData,
            ExtractionResult result,
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

                foreach (var targetMethod in ResolveDelegateTargetMethods(semanticModel, arguments[i].Expression))
                {
                    result.MethodCalls.Add(new MethodCallData
                    {
                        CallerId = symbolData.Fqn,
                        CalleeId = targetMethod.OriginalDefinition.ToDisplayString(),
                        CallCount = 1,
                        CallType = "delegate_reference"
                    });
                }
            }
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
                Kind = "lambda",
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

        private static IMethodSymbol? ResolveCalledMethodSymbol(SymbolInfo symbolInfo)
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
                    CallType = call.CallType == "calls" ? "lambda_dispatch" : call.CallType
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
            VisitNamespace(compilation.GlobalNamespace, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods);
            return new CompilationAnalysisCache(dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods);
        }

        private void VisitNamespace(INamespaceSymbol ns, Dictionary<string, HashSet<string>> dispatchMap, Dictionary<string, List<MethodLookupEntry>> methodLookup, HashSet<string> autofacModuleTypes, HashSet<string> autofacModuleLoadMethods)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNs)
                {
                    VisitNamespace(childNs, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods);
                }
                else if (member is INamedTypeSymbol namedType)
                {
                    VisitType(namedType, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods);
                }
            }
        }

        private void VisitType(INamedTypeSymbol type, Dictionary<string, HashSet<string>> dispatchMap, Dictionary<string, List<MethodLookupEntry>> methodLookup, HashSet<string> autofacModuleTypes, HashSet<string> autofacModuleLoadMethods)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                VisitType(nested, dispatchMap, methodLookup, autofacModuleTypes, autofacModuleLoadMethods);
            }

            var isAutofacModule = IsOrDerivedFrom(type, "Autofac.Module");
            if (isAutofacModule)
            {
                autofacModuleTypes.Add(type.OriginalDefinition.ToDisplayString());
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

        private static void AddDispatchTarget(Dictionary<string, HashSet<string>> dispatchMap, string contractFqn, string implementationFqn)
        {
            if (!dispatchMap.TryGetValue(contractFqn, out var targets))
            {
                targets = new HashSet<string>(StringComparer.Ordinal);
                dispatchMap[contractFqn] = targets;
            }

            targets.Add(implementationFqn);
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

        public CompilationAnalysisCache(
            Dictionary<string, HashSet<string>> dispatchMap,
            Dictionary<string, List<MethodLookupEntry>> methodLookup,
            HashSet<string> autofacModuleTypes,
            HashSet<string> autofacModuleLoadMethods)
        {
            _dispatchMap = dispatchMap;
            _methodLookup = methodLookup;
            _knownMethods = methodLookup.Values
                .SelectMany(methods => methods)
                .Select(method => method.Fqn)
                .ToHashSet(StringComparer.Ordinal);
            _autofacModuleTypes = autofacModuleTypes;
            _autofacModuleLoadMethods = autofacModuleLoadMethods;
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
    }

    internal sealed class MethodLookupEntry
    {
        public string Fqn { get; set; } = "";
        public int ParameterCount { get; set; }
    }
}
