using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;

namespace Probe.Services.Analysis
{
    internal static class FieldAccessExtractor
    {
        /// <summary>
        /// Extract all field and property accesses within a method body.
        /// Tracks read vs write, and whether the access is to an external class.
        /// </summary>
        public static void ExtractFieldAccesses(SemanticModel semanticModel, SyntaxNode methodNode, SymbolData symbolData, ExtractionResult result)
        {
            var containingTypeFqn = symbolData.ContainingType;

            foreach (var identNode in SymbolExtractor.GetAnalysisDescendantNodes(methodNode).OfType<IdentifierNameSyntax>())
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

        private static void RecordFieldAccess(ISymbol accessedSymbol, INamedTypeSymbol? ownerType, IdentifierNameSyntax identNode, string accessorFqn, string? containingTypeFqn, ExtractionResult result)
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

        private static string DetermineAccessKind(SyntaxNode node)
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
    }
}
