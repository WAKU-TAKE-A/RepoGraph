using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Probe.Services.Analysis
{
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
