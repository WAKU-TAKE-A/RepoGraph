using System.Collections.Generic;

namespace Probe.Services.Analysis.Dsl
{
    /// <summary>
    /// Converts collected SymbolData records into generic source/target dictionaries
    /// suitable for the DSL candidate extractor. This is a post-extraction adapter;
    /// it must not contain framework-specific logic.
    /// </summary>
    public static class DslSymbolDataAdapter
    {
        /// <summary>
        /// Converts a single SymbolData into a generic record for the DSL pipeline.
        /// </summary>
        public static IReadOnlyDictionary<string, object?> FromSymbolData(SymbolData symbol)
        {
            var record = new Dictionary<string, object?>
            {
                ["source_type"]          = "csharp_symbol",
                ["fqn"]                  = symbol.Fqn,
                ["name"]                 = symbol.Name,
                ["kind"]                 = symbol.Kind,
                ["containing_type"]      = symbol.ContainingType ?? string.Empty,
                ["containing_namespace"] = symbol.Namespace ?? string.Empty,
                ["parameter_count"]      = symbol.ParameterCount.ToString(),
                ["return_type"]          = symbol.ReturnType ?? string.Empty,
                ["is_static"]            = symbol.IsStatic ? "true" : "false",
            };

            return record;
        }

        /// <summary>
        /// Converts a collection of SymbolData into generic records for the DSL pipeline.
        /// </summary>
        public static List<IReadOnlyDictionary<string, object?>> FromSymbolDataCollection(
            IEnumerable<SymbolData> symbols)
        {
            var records = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var symbol in symbols)
            {
                records.Add(FromSymbolData(symbol));
            }
            return records;
        }
    }
}
