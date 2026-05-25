using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslEndToEndTests
    {
        private static readonly string FixturesDir = Path.Combine(
            Path.GetDirectoryName(typeof(DslEndToEndTests).Assembly.Location)!,
            "Fixtures");

        private DslCandidateExtractor CreateExtractor()
        {
            var conditionEvaluator = new DslConditionEvaluator();
            var bindingEvaluator = new DslBindingEvaluator();
            var targetResolver = new DslTargetResolver(conditionEvaluator);
            var emitter = new DslCandidateEmitter();
            return new DslCandidateExtractor(conditionEvaluator, bindingEvaluator, targetResolver, emitter);
        }

        private DslRule LoadFixture(string fileName)
        {
            var path = Path.Combine(FixturesDir, fileName);
            var json = File.ReadAllText(path, Encoding.UTF8);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var rule = JsonSerializer.Deserialize<DslRule>(json, options);
            Assert.NotNull(rule);
            return rule!;
        }

        [Fact]
        public void E2E_Lifecycle_SymbolConvention()
        {
            var rule = LoadFixture("lifecycle.json");
            var extractor = CreateExtractor();

            var sources = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { { "source_type", "framework_synthetic" }, { "fqn", "ASP.NET_Core" } }
            };

            var targets = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "csharp_symbol" }, 
                    { "fqn", "App.Startup.ConfigureServices()" },
                    { "kind", "method" },
                    { "name", "ConfigureServices" },
                    { "containing_type", "Startup" }
                }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Empty(result.Diagnostics);
            Assert.Single(result.Extraction.MethodCalls);
            var call = result.Extraction.MethodCalls.First();
            Assert.Equal("ASP.NET_Core", call.CallerId);
            Assert.Equal("App.Startup.ConfigureServices()", call.CalleeId);
            Assert.Equal("candidate", call.RuleMode);
            Assert.Equal("lifecycle", call.RuleFamily);
            Assert.Equal("aspnet_startup", call.RuleId);
            Assert.Equal("calls", call.CallType);
        }

        [Fact]
        public void E2E_Lifecycle_ScopeFiltering_RejectsWrongSourceType()
        {
            // scope.source = "framework_synthetic" なのに csharp_symbol を渡す → エッジなし
            var rule = LoadFixture("lifecycle.json");
            var extractor = CreateExtractor();

            var sources = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { { "source_type", "csharp_symbol" }, { "fqn", "ASP.NET_Core" } }
            };

            var targets = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "csharp_symbol" }, 
                    { "fqn", "App.Startup.ConfigureServices()" },
                    { "kind", "method" },
                    { "name", "ConfigureServices" },
                    { "containing_type", "Startup" }
                }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Empty(result.Extraction.MethodCalls);
        }

        [Fact]
        public void E2E_XamlEvent_MarkupConvention()
        {
            var rule = LoadFixture("markup.json");
            var extractor = CreateExtractor();

            var sources = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "xml_attribute" }, 
                    { "fqn", "MainWindow.xaml:Button" }, 
                    { "name", "Click" },
                    { "value", "Save_Click" },
                    { "document.x_class", "App.MainWindow" }
                }
            };

            var targets = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "csharp_symbol" }, 
                    { "fqn", "App.MainWindow.Save_Click(object)" },
                    { "kind", "method" },
                    { "name", "Save_Click" },
                    { "containing_type", "MainWindow" }
                }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Empty(result.Diagnostics);
            Assert.Single(result.Extraction.MethodCalls);
            var call = result.Extraction.MethodCalls.First();
            Assert.Equal("MainWindow.xaml:Button", call.CallerId);
            Assert.Equal("App.MainWindow.Save_Click(object)", call.CalleeId);
            Assert.Equal("candidate", call.RuleMode);
            Assert.Equal("markup", call.RuleFamily);
            Assert.Equal("xaml_event", call.RuleId);
            Assert.Equal("calls", call.CallType);
        }

        [Fact]
        public void E2E_XamlEvent_ScopeFiltering_RejectsWrongTargetType()
        {
            // scope.target = "csharp_symbol" なのに framework_synthetic しか渡さない → エッジなし
            var rule = LoadFixture("markup.json");
            var extractor = CreateExtractor();

            var sources = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "xml_attribute" }, 
                    { "fqn", "MainWindow.xaml:Button" }, 
                    { "name", "Click" },
                    { "value", "Save_Click" },
                    { "document.x_class", "App.MainWindow" }
                }
            };

            var targets = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    // Wrong target type
                    { "source_type", "framework_synthetic" }, 
                    { "fqn", "App.MainWindow.Save_Click(object)" },
                    { "kind", "method" },
                    { "name", "Save_Click" },
                    { "containing_type", "MainWindow" }
                }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Empty(result.Extraction.MethodCalls);
        }

        [Fact]
        public void E2E_DIGenericResolution_InvocationConvention()
        {
            var rule = LoadFixture("di.json");
            var extractor = CreateExtractor();

            var sources = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "csharp_invocation" }, 
                    { "fqn", "App.Consumer.DoWork()" },
                    { "method.name", "GetRequiredService" },
                    { "invocation.generic_arg[0]", "FooService" }
                }
            };

            var targets = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "csharp_symbol" }, 
                    { "fqn", "App.Services.FooService..ctor()" },
                    { "kind", "method" },
                    { "name", ".ctor" },
                    { "containing_type", "FooService" }
                }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Empty(result.Diagnostics);
            Assert.Single(result.Extraction.MethodCalls);
            var call = result.Extraction.MethodCalls.First();
            Assert.Equal("App.Consumer.DoWork()", call.CallerId);
            Assert.Equal("App.Services.FooService..ctor()", call.CalleeId);
            Assert.Equal("candidate", call.RuleMode);
            Assert.Equal("di", call.RuleFamily);
            Assert.Equal("di_get_required_service", call.RuleId);
            Assert.Equal("calls", call.CallType);
        }

        [Fact]
        public void E2E_SerializationCallback_MetadataConvention()
        {
            var rule = LoadFixture("metadata.json");
            var extractor = CreateExtractor();

            var sources = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "csharp_symbol" }, 
                    { "fqn", "App.Models.Person.OnDeserialized(StreamingContext)" },
                    { "kind", "method" },
                    { "name", "OnDeserialized" },
                    { "has_callback_attribute", "true" }
                }
            };

            var targets = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { 
                    { "source_type", "framework_synthetic" }, 
                    { "fqn", "System.Runtime.Serialization" }
                }
            };

            var result = extractor.Extract(new[] { rule }, sources, targets);

            Assert.Empty(result.Diagnostics);
            Assert.Single(result.Extraction.MethodCalls);
            var call = result.Extraction.MethodCalls.First();
            Assert.Equal("App.Models.Person.OnDeserialized(StreamingContext)", call.CallerId);
            Assert.Equal("System.Runtime.Serialization", call.CalleeId);
            Assert.Equal("candidate", call.RuleMode);
            Assert.Equal("metadata", call.RuleFamily);
            Assert.Equal("on_deserialized", call.RuleId);
            Assert.Equal("calls", call.CallType);
        }
    }
}
