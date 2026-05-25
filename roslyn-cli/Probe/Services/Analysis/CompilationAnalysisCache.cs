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

        public CompilationAnalysisCache(
            Dictionary<string, HashSet<string>> dispatchMap,
            Dictionary<string, List<MethodLookupEntry>> methodLookup)
        {
            _dispatchMap = dispatchMap;
            _methodLookup = methodLookup;
            _knownMethods = methodLookup.Values
                .SelectMany(methods => methods)
                .Select(method => method.Fqn)
                .ToHashSet(StringComparer.Ordinal);
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
    }

    internal sealed class MethodLookupEntry
    {
        public string Fqn { get; set; } = "";
        public int ParameterCount { get; set; }
    }
}
