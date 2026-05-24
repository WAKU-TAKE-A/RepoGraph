using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace Probe.Services.Analysis
{
    internal static class TypeDependencyExtractor
    {
        public static void ExtractTypeDependencies(SemanticModel semanticModel, SyntaxNode node, SymbolData symbolData, ExtractionResult result)
        {
            foreach (var typeNode in SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<TypeSyntax>())
            {
                var typeInfo = semanticModel.GetTypeInfo(typeNode);
                RecordTypeDependency(typeInfo.Type, symbolData.Fqn, result);
            }

            // Also check for 'new', 'as', 'is', and casts
            foreach (var creation in SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<ObjectCreationExpressionSyntax>())
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

            foreach (var implicitCreation in SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                var createdType = semanticModel.GetTypeInfo(implicitCreation).Type;
                RecordTypeDependency(createdType, symbolData.Fqn, result);
            }

            foreach (var cast in SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<CastExpressionSyntax>())
            {
                var typeInfo = semanticModel.GetTypeInfo(cast.Type);
                RecordTypeDependency(typeInfo.Type, symbolData.Fqn, result);
            }

            foreach (var binary in SymbolExtractor.GetAnalysisDescendantNodes(node).OfType<BinaryExpressionSyntax>())
            {
                if (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression))
                {
                    var typeInfo = semanticModel.GetTypeInfo(binary.Right);
                    RecordTypeDependency(typeInfo.Type, symbolData.Fqn, result);
                }
            }
        }

        public static void RecordTypeDependency(ITypeSymbol? type, string sourceFqn, ExtractionResult result)
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
    }
}
