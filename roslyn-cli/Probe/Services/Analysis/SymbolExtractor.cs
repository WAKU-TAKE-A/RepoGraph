using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Probe.Services.Analysis
{
    public class SymbolExtractor
    {
        private readonly ILogger<SymbolExtractor> _logger;

        public SymbolExtractor(ILogger<SymbolExtractor> logger)
        {
            _logger = logger;
        }

        public ExtractionResult Extract(Compilation compilation, SyntaxTree tree)
        {
            var result = new ExtractionResult();
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                ISymbol? symbol = null;
                
                if (node is BaseTypeDeclarationSyntax typeDecl)
                {
                    symbol = semanticModel.GetDeclaredSymbol(typeDecl);
                }
                else if (node is MethodDeclarationSyntax methodDecl)
                {
                    symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                }
                else if (node is PropertyDeclarationSyntax propDecl)
                {
                    symbol = semanticModel.GetDeclaredSymbol(propDecl);
                }
                else if (node is FieldDeclarationSyntax fieldDecl)
                {
                    // Fields can have multiple variables, handle first for simplicity or all
                    var variable = fieldDecl.Declaration.Variables.FirstOrDefault();
                    if (variable != null)
                        symbol = semanticModel.GetDeclaredSymbol(variable);
                }

                if (symbol != null)
                {
                    var symbolData = MapToData(symbol, node);
                    result.Symbols.Add(symbolData);

                    // Extract inheritance
                    if (symbol is INamedTypeSymbol namedType)
                    {
                        if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
                        {
                            result.Inheritances.Add(new InheritanceData
                            {
                                DerivedId = symbolData.Fqn,
                                BaseId = namedType.BaseType.ToDisplayString(),
                                Kind = "extends"
                            });
                        }
                        foreach (var iface in namedType.Interfaces)
                        {
                            result.Inheritances.Add(new InheritanceData
                            {
                                DerivedId = symbolData.Fqn,
                                BaseId = iface.ToDisplayString(),
                                Kind = "implements"
                            });
                        }
                    }

                    // Extract method calls, thread boundaries, field accesses, and event subscriptions
                    if (node is MethodDeclarationSyntax methodNode && symbol is IMethodSymbol methodSymbol)
                    {
                        ExtractMethodCalls(semanticModel, methodNode, symbolData, result);
                        ExtractThreadBoundaries(semanticModel, methodNode, symbolData);
                        ExtractFieldAccesses(semanticModel, methodNode, symbolData, result);
                        ExtractEventSubscriptions(semanticModel, methodNode, symbolData, result);
                    }
                }
            }

            // Aggregate call counts
            result.MethodCalls = result.MethodCalls
                .GroupBy(c => new { c.CallerId, c.CalleeId })
                .Select(g => new MethodCallData
                {
                    CallerId = g.Key.CallerId,
                    CalleeId = g.Key.CalleeId,
                    CallCount = g.Sum(x => x.CallCount)
                }).ToList();

            // Deduplicate field accesses (upgrade read to read_write if both read and write exist)
            result.FieldAccesses = DeduplicateFieldAccesses(result.FieldAccesses);

            return result;
        }

        /// <summary>
        /// Extract method invocation calls.
        /// </summary>
        private void ExtractMethodCalls(SemanticModel semanticModel, MethodDeclarationSyntax methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var invocations = methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                {
                    result.MethodCalls.Add(new MethodCallData
                    {
                        CallerId = symbolData.Fqn,
                        CalleeId = calledMethod.OriginalDefinition.ToDisplayString(),
                        CallCount = 1
                    });
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
        private void ExtractThreadBoundaries(SemanticModel semanticModel, MethodDeclarationSyntax methodNode, SymbolData symbolData)
        {
            // Check for lock statements
            symbolData.HasLock = methodNode.DescendantNodes().OfType<LockStatementSyntax>().Any();

            var invocations = methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                {
                    var methodName = calledMethod.Name;
                    var containingTypeFqn = calledMethod.ContainingType?.ToDisplayString() ?? "";

                    // Invoke / BeginInvoke on Control (UI thread dispatch)
                    if ((methodName == "Invoke" || methodName == "BeginInvoke") &&
                        IsOrDerivedFrom(calledMethod.ContainingType, "System.Windows.Forms.Control"))
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
                    if (methodText.EndsWith(".Invoke") || methodText.EndsWith(".BeginInvoke"))
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
            var memberAccesses = methodNode.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
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
        private void ExtractFieldAccesses(SemanticModel semanticModel, MethodDeclarationSyntax methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var containingTypeFqn = symbolData.ContainingType;

            foreach (var identNode in methodNode.DescendantNodes().OfType<IdentifierNameSyntax>())
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
                return "write";

            // Member access on left of assignment: obj.Field = value
            if (parent is MemberAccessExpressionSyntax memberAccess)
            {
                var grandParent = memberAccess.Parent;
                if (grandParent is AssignmentExpressionSyntax outerAssignment && outerAssignment.Left == memberAccess)
                    return "write";
            }

            // Prefix/postfix increment/decrement: x++ / --x
            if (parent is PostfixUnaryExpressionSyntax || parent is PrefixUnaryExpressionSyntax)
            {
                var kind = parent.Kind();
                if (kind == SyntaxKind.PostIncrementExpression || kind == SyntaxKind.PostDecrementExpression ||
                    kind == SyntaxKind.PreIncrementExpression || kind == SyntaxKind.PreDecrementExpression)
                    return "write";
            }

            // ref / out argument
            if (parent is ArgumentSyntax arg && (arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
                return "write";

            return "read";
        }

        /// <summary>
        /// Detect event handler subscriptions (+=) and unsubscriptions (-=).
        /// Records them as special method calls with type "event_subscribe" / "event_unsubscribe".
        /// </summary>
        private void ExtractEventSubscriptions(SemanticModel semanticModel, MethodDeclarationSyntax methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var assignments = methodNode.DescendantNodes().OfType<AssignmentExpressionSyntax>();
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
                }
            }
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
                IsPartial = false,
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

    public class InheritanceData
    {
        public string DerivedId { get; set; } = "";
        public string BaseId { get; set; } = "";
        public string Kind { get; set; } = "";
    }

    public class ExtractionResult
    {
        public List<SymbolData> Symbols { get; set; } = new List<SymbolData>();
        public List<MethodCallData> MethodCalls { get; set; } = new List<MethodCallData>();
        public List<InheritanceData> Inheritances { get; set; } = new List<InheritanceData>();
        public List<FieldAccessData> FieldAccesses { get; set; } = new List<FieldAccessData>();
    }
}
