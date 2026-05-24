using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Probe.Services.Analysis
{
    internal static class ReflectionDispatchExtractor
    {
        public static bool TryExtractConstructorDispatch(
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


        public static bool TryResolveConstructorInfoDispatch(
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


        public static bool TryResolveReflectedConstructorTargets(
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


        public static HashSet<string> ResolveReflectedTypeCandidates(
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


        public static bool TryFindLatestAssignmentExpression(
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
                foreach (var declarator in SymbolExtractor.GetAnalysisDescendantNodes(searchScope).OfType<VariableDeclaratorSyntax>())
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

                foreach (var assignment in SymbolExtractor.GetAnalysisDescendantNodes(searchScope).OfType<AssignmentExpressionSyntax>())
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


        public static IEnumerable<SyntaxNode> GetAssignmentSearchScopes(SyntaxNode scopeNode)
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


        public static IEnumerable<string> ResolveReflectedTypeDescriptorArguments(
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


        public static IEnumerable<string> ResolveTypeDescriptorNamesFromExpression(
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


        public static IEnumerable<string> ResolveRuntimeArgumentTypes(SemanticModel semanticModel, IEnumerable<ArgumentSyntax> arguments)
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


        public static ITypeSymbol? ResolveEffectiveTypeSymbol(
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


        public static bool TryResolveConstructedTypeParameter(
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


        public static HashSet<string> ApplyForeachTypeGuards(
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


        public static bool IsContinueOnly(StatementSyntax statement)
        {
            if (statement is ContinueStatementSyntax)
            {
                return true;
            }

            return statement is BlockSyntax block &&
                   block.Statements.Count == 1 &&
                   block.Statements[0] is ContinueStatementSyntax;
        }


        public static ExpressionSyntax NegateCondition(ExpressionSyntax condition)
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


        public static AnonymousFunctionExpressionSyntax? ExtractLambda(ExpressionSyntax expression)
        {
            expression = UnwrapExpression(expression);
            return expression as AnonymousFunctionExpressionSyntax;
        }


        public static string? GetSingleLambdaParameterName(AnonymousFunctionExpressionSyntax lambda)
        {
            return lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                AnonymousMethodExpressionSyntax anonymousMethod when anonymousMethod.ParameterList?.Parameters.Count == 1 => anonymousMethod.ParameterList.Parameters[0].Identifier.ValueText,
                _ => null
            };
        }


        public static bool EvaluateTypePredicate(
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


        public static bool EvaluateTypePredicateExpression(
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


        public static bool TryResolveTypeMemberBoolean(
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


        public static bool TryResolveHasConstructorPredicate(
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


        public static bool TryResolveTypeMemberString(
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


        public static bool TryResolveStringLiteral(SyntaxNode expression, out string value)
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


        public static bool IsNullLiteral(SyntaxNode expression)
        {
            if (expression is ExpressionSyntax expressionSyntax)
            {
                expression = UnwrapExpression(expressionSyntax);
            }

            return expression is LiteralExpressionSyntax literal &&
                   literal.IsKind(SyntaxKind.NullLiteralExpression);
        }


        public static bool TryEvaluateAssignableFromPredicate(
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


        public static bool IsConstructorInfoInvoke(IMethodSymbol? calledMethod)
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


        public static bool LooksLikeConstructorInvokeSyntax(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   string.Equals(memberAccess.Name.Identifier.ValueText, "Invoke", StringComparison.Ordinal) &&
                   memberAccess.Expression is IdentifierNameSyntax;
        }


        public static bool LooksLikeGetConstructorSyntax(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   string.Equals(memberAccess.Name.Identifier.ValueText, "GetConstructor", StringComparison.Ordinal);
        }


        public static bool LooksLikeActivatorCreateInstanceSyntax(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   memberAccess.Expression is IdentifierNameSyntax identifier &&
                   string.Equals(identifier.Identifier.ValueText, "Activator", StringComparison.Ordinal) &&
                   string.Equals(memberAccess.Name.Identifier.ValueText, "CreateInstance", StringComparison.Ordinal);
        }


        public static bool IsActivatorCreateInstance(IMethodSymbol? calledMethod)
        {
            return calledMethod != null &&
                   string.Equals(calledMethod.Name, "CreateInstance", StringComparison.Ordinal) &&
                   string.Equals(calledMethod.ContainingType?.ToDisplayString(), "System.Activator", StringComparison.Ordinal);
        }


        public static ExpressionSyntax UnwrapExpression(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }


    }
}
