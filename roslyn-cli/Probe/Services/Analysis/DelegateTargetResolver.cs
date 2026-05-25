using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Probe.Services.Analysis
{
    internal static class DelegateTargetResolver
    {
        public static IEnumerable<IMethodSymbol> ResolveDelegateTargetMethods(SemanticModel semanticModel, ExpressionSyntax expression)
        {
            var unwrappedExpression = UnwrapExpression(expression);

            if (TryResolveMethodGroupByLookup(semanticModel, unwrappedExpression, out var lookedUpMethods))
            {
                foreach (var method in lookedUpMethods)
                {
                    yield return method;
                }

                yield break;
            }

            if (semanticModel.GetOperation(unwrappedExpression) is IDelegateCreationOperation delegateCreation)
            {
                foreach (var method in ResolveDelegateTargetMethodsFromOperation(delegateCreation.Target))
                {
                    yield return method;
                }

                yield break;
            }

            if (semanticModel.GetOperation(unwrappedExpression) is IMethodReferenceOperation methodReference &&
                methodReference.Method != null)
            {
                yield return methodReference.Method;
                yield break;
            }

            if (unwrappedExpression is AnonymousFunctionExpressionSyntax anonymousFunction)
            {
                var anonymousSymbol = semanticModel.GetOperation(anonymousFunction) is IAnonymousFunctionOperation anonymousOperation
                    ? anonymousOperation.Symbol
                    : null;
                if (anonymousSymbol != null)
                {
                    yield return anonymousSymbol;
                    yield break;
                }
            }

            var directInfo = semanticModel.GetSymbolInfo(unwrappedExpression);
            if (directInfo.Symbol is IMethodSymbol directMethod)
            {
                yield return directMethod;
                yield break;
            }

            foreach (var candidate in directInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                yield return candidate;
            }

            if (unwrappedExpression is ObjectCreationExpressionSyntax creation &&
                creation.ArgumentList is { Arguments.Count: > 0 })
            {
                foreach (var argument in creation.ArgumentList.Arguments)
                {
                    if (semanticModel.GetOperation(argument.Expression) is IMethodReferenceOperation nestedReference &&
                        nestedReference.Method != null)
                    {
                        yield return nestedReference.Method;
                        continue;
                    }

                    var argInfo = semanticModel.GetSymbolInfo(argument.Expression);
                    if (argInfo.Symbol is IMethodSymbol method)
                    {
                        yield return method;
                    }
                    else
                    {
                        foreach (var candidate in argInfo.CandidateSymbols.OfType<IMethodSymbol>())
                        {
                            yield return candidate;
                        }
                    }
                }
            }
        }

        private static bool TryResolveMethodGroupByLookup(
            SemanticModel semanticModel,
            ExpressionSyntax expression,
            out IEnumerable<IMethodSymbol> methods)
        {
            methods = Enumerable.Empty<IMethodSymbol>();
            string? methodName = null;

            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    methodName = identifier.Identifier.ValueText;
                    break;

                case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax or BaseExpressionSyntax:
                    methodName = memberAccess.Name.Identifier.ValueText;
                    break;
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                return false;
            }

            var lookedUp = semanticModel.LookupSymbols(expression.SpanStart, name: methodName)
                .OfType<IMethodSymbol>()
                .ToList();

            if (lookedUp.Count == 0)
            {
                lookedUp = ResolveMethodGroupByContainingTypeSyntax(semanticModel, expression, methodName).ToList();
                if (lookedUp.Count == 0)
                {
                    return false;
                }
            }

            methods = lookedUp;
            return true;
        }

        private static IEnumerable<IMethodSymbol> ResolveMethodGroupByContainingTypeSyntax(
            SemanticModel semanticModel,
            SyntaxNode expression,
            string methodName)
        {
            var containingType = expression.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (containingType == null)
            {
                yield break;
            }

            foreach (var methodDeclaration in containingType.Members
                         .OfType<MethodDeclarationSyntax>()
                         .Where(method => string.Equals(method.Identifier.ValueText, methodName, StringComparison.Ordinal)))
            {
                var symbol = semanticModel.GetDeclaredSymbol(methodDeclaration);
                if (symbol != null)
                {
                    yield return symbol;
                }
            }
        }

        private static IEnumerable<IMethodSymbol> ResolveDelegateTargetMethodsFromOperation(IOperation? operation)
        {
            if (operation == null)
            {
                yield break;
            }

            if (operation is IMethodReferenceOperation methodReference && methodReference.Method != null)
            {
                yield return methodReference.Method;
                yield break;
            }

            if (operation is IAnonymousFunctionOperation anonymousFunction && anonymousFunction.Symbol != null)
            {
                yield return anonymousFunction.Symbol;
            }
        }

        private static ExpressionSyntax UnwrapExpression(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }
    }
}
