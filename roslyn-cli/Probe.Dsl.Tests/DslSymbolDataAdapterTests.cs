using System.Collections.Generic;
using Probe.Services.Analysis;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslSymbolDataAdapterTests
    {
        [Fact]
        public void FromSymbolData_MapsFieldsCorrectly()
        {
            var symbol = new SymbolData
            {
                Fqn = "MyNamespace.MyClass.MyMethod(int, string)",
                Name = "MyMethod",
                Kind = "method",
                ContainingType = "MyNamespace.MyClass",
                Namespace = "MyNamespace",
                ParameterCount = 2,
                ReturnType = "void",
                IsStatic = true
            };

            var record = DslSymbolDataAdapter.FromSymbolData(symbol);

            Assert.Equal("csharp_symbol", record["source_type"]);
            Assert.Equal("MyNamespace.MyClass.MyMethod(int, string)", record["fqn"]);
            Assert.Equal("MyMethod", record["name"]);
            Assert.Equal("method", record["kind"]);
            Assert.Equal("MyNamespace.MyClass", record["containing_type"]);
            Assert.Equal("MyNamespace", record["containing_namespace"]);
            Assert.Equal("2", record["parameter_count"]);
            Assert.Equal("void", record["return_type"]);
            Assert.Equal("true", record["is_static"]);
        }

        [Fact]
        public void FromSymbolData_HandlesNullsSafely()
        {
            var symbol = new SymbolData
            {
                Fqn = "GlobalMethod()",
                Name = "GlobalMethod",
                Kind = "method",
                ContainingType = null,
                Namespace = null,
                ParameterCount = 0,
                ReturnType = null,
                IsStatic = false
            };

            var record = DslSymbolDataAdapter.FromSymbolData(symbol);

            Assert.Equal("csharp_symbol", record["source_type"]);
            Assert.Equal("GlobalMethod()", record["fqn"]);
            Assert.Equal("GlobalMethod", record["name"]);
            Assert.Equal("method", record["kind"]);
            Assert.Equal("", record["containing_type"]);
            Assert.Equal("", record["containing_namespace"]);
            Assert.Equal("0", record["parameter_count"]);
            Assert.Equal("", record["return_type"]);
            Assert.Equal("false", record["is_static"]);
        }

        [Fact]
        public void FromSymbolDataCollection_MapsAllSymbols()
        {
            var symbols = new List<SymbolData>
            {
                new SymbolData { Fqn = "A", Name = "A", Kind = "class" },
                new SymbolData { Fqn = "B", Name = "B", Kind = "method" }
            };

            var records = DslSymbolDataAdapter.FromSymbolDataCollection(symbols);

            Assert.Equal(2, records.Count);
            Assert.Equal("A", records[0]["fqn"]);
            Assert.Equal("class", records[0]["kind"]);
            Assert.Equal("B", records[1]["fqn"]);
            Assert.Equal("method", records[1]["kind"]);
        }
    }
}
