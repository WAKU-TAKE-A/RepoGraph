using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace Probe.Services.Analysis
{
    internal static class ThreadBoundaryExtractor
    {
        /// <summary>
        /// Detect thread boundary patterns in method body:
        /// - Invoke/BeginInvoke (UI thread dispatch)
        /// - Task.Run / Task.Factory.StartNew (background thread spawn)
        /// - BackgroundWorker usage
        /// - Application.DoEvents() (re-entrancy hazard)
        /// - lock statements (mutual exclusion)
        /// </summary>
        public static void ExtractThreadBoundaries(SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData)
        {
            // Check for lock statements
            symbolData.HasLock = SymbolExtractor.GetAnalysisDescendantNodes(methodNode).OfType<LockStatementSyntax>().Any();

            var invocations = SymbolExtractor.GetAnalysisDescendantNodes(methodNode).OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var calledMethod = DirectCallExtractor.ResolveCalledMethodSymbol(semanticModel.GetSymbolInfo(invocation));
                if (calledMethod != null)
                {
                    var methodName = calledMethod.Name;
                    var containingTypeFqn = calledMethod.ContainingType?.ToDisplayString() ?? "";

                    // Invoke / BeginInvoke on WinForms controls or WPF dispatcher.
                    if ((methodName == "Invoke" || methodName == "BeginInvoke") &&
                        (SymbolExtractor.IsOrDerivedFrom(calledMethod.ContainingType, "System.Windows.Forms.Control") ||
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
                        SymbolExtractor.IsOrDerivedFrom(calledMethod.ContainingType, "System.Threading.SynchronizationContext"))
                    {
                        symbolData.HasUiDispatch = true;
                    }

                    // BackgroundWorker.RunWorkerAsync
                    if (methodName == "RunWorkerAsync" &&
                        SymbolExtractor.IsOrDerivedFrom(calledMethod.ContainingType, "System.ComponentModel.BackgroundWorker"))
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
            var memberAccesses = SymbolExtractor.GetAnalysisDescendantNodes(methodNode).OfType<MemberAccessExpressionSyntax>();
            foreach (var memberAccess in memberAccesses)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
                if (symbolInfo.Symbol is IFieldSymbol field &&
                    SymbolExtractor.IsOrDerivedFrom(field.Type, "System.ComponentModel.BackgroundWorker"))
                {
                    symbolData.HasBackgroundWorker = true;
                    break;
                }
                if (symbolInfo.Symbol is ILocalSymbol local &&
                    SymbolExtractor.IsOrDerivedFrom(local.Type, "System.ComponentModel.BackgroundWorker"))
                {
                    symbolData.HasBackgroundWorker = true;
                    break;
                }
            }
        }
    }
}
