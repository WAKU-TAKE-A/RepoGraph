using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslSourceAdapterTests
    {
        private static Compilation CreateCompilation(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
            var compilation = CSharpCompilation.Create("TestAssembly",
                new[] { syntaxTree },
                new[] { mscorlib },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return compilation;
        }

        [Fact]
        public void CSharpSourceAdapter_FromSymbol_PopulatesFields()
        {
            var source = @"
namespace TestNamespace
{
    public class TestClass
    {
        public static string TestMethod(int arg) { return """"; }
    }
}";
            var comp = CreateCompilation(source);
            var symbol = comp.GetTypeByMetadataName("TestNamespace.TestClass")!
                             .GetMembers("TestMethod").First() as IMethodSymbol;

            Assert.NotNull(symbol);
            
            var dict = DslCSharpSourceAdapter.FromSymbol(symbol!);
            
            Assert.Equal("csharp_symbol", dict["source_type"]);
            Assert.Equal("TestNamespace.TestClass.TestMethod(System.Int32)", dict["fqn"]);
            Assert.Equal("TestMethod", dict["name"]);
            Assert.Equal("method", dict["kind"]);
            Assert.Equal("TestClass", dict["containing_type"]);
            Assert.Equal("TestNamespace", dict["containing_namespace"]);
            Assert.Equal(true, dict["is_static"]);
            Assert.Equal(1, dict["parameter_count"]);
            Assert.Equal("System.String", dict["return_type"]);
        }

        [Fact]
        public void CSharpSourceAdapter_FromInvocation_PopulatesFields()
        {
            var source = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void CallTarget<T>(string s) { }
        public void Run() 
        { 
            CallTarget<int>(""test""); 
        }
    }
}";
            var comp = CreateCompilation(source);
            var tree = comp.SyntaxTrees.First();
            var model = comp.GetSemanticModel(tree);
            var root = tree.GetRoot();
            
            var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>().First();
            
            var dict = DslCSharpSourceAdapter.FromInvocation(invocation, model, "TestNamespace.TestClass.Run()");
            
            Assert.Equal("csharp_invocation", dict["source_type"]);
            Assert.Equal("TestNamespace.TestClass.Run()", dict["fqn"]);
            Assert.Equal(1, dict["argument_count"]);
            Assert.Equal("CallTarget", dict["method.name"]);
            Assert.Equal("TestNamespace.TestClass.CallTarget<System.Int32>(System.String)", dict["method.fqn"]);
            Assert.Equal("TestClass", dict["method.containing_type"]);
            Assert.Equal("TestNamespace", dict["method.containing_namespace"]);
            Assert.Equal(1, dict["invocation.generic_arg_count"]);
            Assert.Equal("Int32", dict["invocation.generic_arg[0]"]);
        }

        [Fact]
        public void XmlSourceAdapter_FromAttribute_PopulatesFields()
        {
            var xml = @"<Button Click=""OnClick"" />";
            var root = XElement.Parse(xml);
            var attribute = root.Attribute("Click");

            Assert.NotNull(attribute);

            var dict = DslXmlSourceAdapter.FromAttribute(attribute!, "MainWindow.xaml", "App.MainWindow");

            Assert.Equal("xml_attribute", dict["source_type"]);
            Assert.Equal("Click", dict["name"]);
            Assert.Equal("OnClick", dict["value"]);
            Assert.Equal("MainWindow.xaml", dict["document.path"]);
            Assert.Equal("App.MainWindow", dict["document.x_class"]);
            Assert.Equal("Button", dict["element.name"]);
        }
    }
}
