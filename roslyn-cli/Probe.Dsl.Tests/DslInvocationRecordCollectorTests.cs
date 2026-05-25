using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslInvocationRecordCollectorTests
    {
        [Fact]
        public void Collect_ExtractsInvocationsWithCallerFqn()
        {
            var code = @"
namespace TestNamespace
{
    class TestClass
    {
        void MethodA()
        {
            MethodB(1, ""test"");
        }
        void MethodB(int i, string s) { }
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(code);
            var compilation = CSharpCompilation.Create("TestDb")
                .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddSyntaxTrees(tree);

            var model = compilation.GetSemanticModel(tree);
            var records = DslInvocationRecordCollector.Collect(model, tree.GetRoot());

            Assert.Single(records);
            var record = records[0];
            Assert.Equal("csharp_invocation", record["source_type"]);
            Assert.Equal("TestNamespace.TestClass.MethodA()", record["fqn"]);
            Assert.Equal(2, record["argument_count"]);
            Assert.Equal("MethodB", record["method.name"]);
            Assert.Equal("TestNamespace.TestClass.MethodB(System.Int32, System.String)", record["method.fqn"]);
            Assert.Equal("TestClass", record["method.containing_type"]);
            Assert.Equal("TestNamespace", record["method.containing_namespace"]);
        }

        [Fact]
        public void Collect_UnresolvedInvocation_FallsBackToSyntaxFields()
        {
            // Missing references will cause MethodC to be unresolved
            var code = @"
class TestClass
{
    void MethodA()
    {
        MethodC();
    }
}
";
            var tree = CSharpSyntaxTree.ParseText(code);
            var compilation = CSharpCompilation.Create("TestDb")
                .AddSyntaxTrees(tree);

            var model = compilation.GetSemanticModel(tree);
            var records = DslInvocationRecordCollector.Collect(model, tree.GetRoot());

            Assert.Single(records);
            var record = records[0];
            Assert.Equal("csharp_invocation", record["source_type"]);
            Assert.Equal("TestClass.MethodA()", record["fqn"]);
            Assert.Equal(0, record["argument_count"]);
            Assert.Equal("MethodC", record["method.name"]);
            Assert.False(record.ContainsKey("method.fqn"));
        }
    }
}
