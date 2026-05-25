using System;
using System.IO;
using System.Linq;
using Probe.Services.Analysis.Dsl;
using Xunit;

namespace Probe.Dsl.Tests
{
    public class DslXmlAttributeRecordCollectorTests : IDisposable
    {
        private readonly string _tempFile;

        public DslXmlAttributeRecordCollectorTests()
        {
            _tempFile = Path.GetTempFileName();
        }

        public void Dispose()
        {
            if (File.Exists(_tempFile))
            {
                File.Delete(_tempFile);
            }
        }

        [Fact]
        public void Collect_ExtractsXmlAttributesWithXClassAndFqn()
        {
            var xml = @"<Window x:Class=""App.MainWindow"" 
        xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" 
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" 
        Title=""TestWindow"">
    <Button Name=""TestButton"" Click=""OnClick"" />
</Window>";
            File.WriteAllText(_tempFile, xml);

            var records = DslXmlAttributeRecordCollector.Collect(new[] { _tempFile });

            // Should collect Class, Title, Name, Click (4 attributes). xmlns attributes should be skipped.
            Assert.Equal(4, records.Count);

            var titleRecord = records.Single(r => (string?)r["name"] == "Title");
            Assert.Equal("xml_attribute", titleRecord["source_type"]);
            Assert.Equal("TestWindow", titleRecord["value"]);
            Assert.Equal("App.MainWindow", titleRecord["document.x_class"]);
            Assert.Equal("Window", titleRecord["element.name"]);
            Assert.Equal("xaml::App.MainWindow::Window::Title", titleRecord["fqn"]);

            var clickRecord = records.Single(r => (string?)r["name"] == "Click");
            Assert.Equal("OnClick", clickRecord["value"]);
            Assert.Equal("App.MainWindow", clickRecord["document.x_class"]);
            Assert.Equal("Button", clickRecord["element.name"]);
            Assert.Equal("xaml::App.MainWindow::Button::Click", clickRecord["fqn"]);
        }

        [Fact]
        public void Collect_WithoutXClass_UsesFilenameForFqn()
        {
            var xml = @"<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <Style TargetType=""Button"" />
</ResourceDictionary>";
            File.WriteAllText(_tempFile, xml);

            var records = DslXmlAttributeRecordCollector.Collect(new[] { _tempFile });

            Assert.Single(records);
            var record = records[0];

            Assert.Equal("TargetType", record["name"]);
            Assert.Null(record["document.x_class"]);
            
            var fileName = Path.GetFileName(_tempFile);
            Assert.Equal($"xaml::{fileName}::Style::TargetType", record["fqn"]);
        }
    }
}
