using System;
using System.IO;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Probe.Config
{
    public class ConfigLoader
    {
        private readonly ILogger<ConfigLoader> _logger;

        public ConfigLoader(ILogger<ConfigLoader> logger)
        {
            _logger = logger;
        }

        public AnalyzerConfig Load(string solutionOrProjectPath)
        {
            var config = new AnalyzerConfig();
            
            try
            {
                var directory = Path.GetDirectoryName(solutionOrProjectPath);
                if (string.IsNullOrEmpty(directory)) return config;

                var configPath = Path.Combine(directory, "analyzer.yml");
                
                if (File.Exists(configPath))
                {
                    _logger.LogInformation("Found analyzer.yml at {Path}", configPath);
                    var yaml = File.ReadAllText(configPath);
                    
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                        
                    config = deserializer.Deserialize<AnalyzerConfig>(yaml) ?? new AnalyzerConfig();
                    _logger.LogInformation("Loaded exclusions: {DirCount} directories, {FileCount} file patterns, {PathCount} paths", 
                        config.Exclude?.Directories?.Count ?? 0,
                        config.Exclude?.FilePatterns?.Count ?? 0,
                        config.Exclude?.Paths?.Count ?? 0);
                }
                else
                {
                    _logger.LogInformation("No analyzer.yml found at {Path}. Using default config.", configPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load analyzer.yml. Falling back to default config.");
            }

            // Ensure not null
            config.Exclude ??= new ExcludeConfig();
            config.Exclude.Directories ??= new System.Collections.Generic.List<string>();
            config.Exclude.FilePatterns ??= new System.Collections.Generic.List<string>();
            config.Exclude.Paths ??= new System.Collections.Generic.List<string>();
            config.Exclude.Namespaces ??= new System.Collections.Generic.List<string>();

            return config;
        }
    }
}
