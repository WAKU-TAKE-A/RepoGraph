using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Probe.Services.Analysis.Dsl
{
    public class DslRuleLoader
    {
        private readonly ILogger<DslRuleLoader> _logger;
        private readonly DslRuleValidator _validator;

        public DslRuleLoader(ILogger<DslRuleLoader> logger, DslRuleValidator validator)
        {
            _logger = logger;
            _validator = validator;
        }

        public DslRuleSet LoadRules(string directoryPath)
        {
            var ruleSet = new DslRuleSet();
            if (!Directory.Exists(directoryPath))
            {
                _logger.LogWarning("DSL rules directory not found: {Path}", directoryPath);
                return ruleSet;
            }

            var files = Directory.GetFiles(directoryPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                    var rule = JsonSerializer.Deserialize<DslRule>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (rule != null)
                    {
                        var validationResult = _validator.Validate(rule);
                        if (validationResult.IsValid)
                        {
                            ruleSet.Rules.Add(rule);
                        }
                        else
                        {
                            _logger.LogWarning("Rule in {File} is invalid: {Errors}", file, string.Join(", ", validationResult.Errors));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load DSL rule from {File}", file);
                }
            }

            return ruleSet;
        }
    }
}
