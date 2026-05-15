using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Probe.Config;

namespace Probe.Services.Analysis
{
    public class FilterService
    {
        private readonly AnalyzerConfig _config;
        private readonly ILogger<FilterService> _logger;

        public FilterService(AnalyzerConfig config, ILogger<FilterService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public bool ShouldExcludeFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            // Normalize path separators to forward slash for easier matching
            var normalizedPath = filePath.Replace("\\", "/");

            // 1. Directory Exclusion
            var segments = normalizedPath.Split('/');
            foreach (var excludedDir in _config.Exclude.Directories)
            {
                if (segments.Contains(excludedDir, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Excluded by directory ({Dir}): {Path}", excludedDir, filePath);
                    return true;
                }
            }

            // 2. File Pattern Exclusion
            var fileName = Path.GetFileName(filePath);
            foreach (var pattern in _config.Exclude.FilePatterns)
            {
                if (MatchesWildcard(fileName, pattern))
                {
                    _logger.LogDebug("Excluded by file pattern ({Pattern}): {Path}", pattern, filePath);
                    return true;
                }
            }

            // 3. Path Glob Exclusion
            foreach (var pathGlob in _config.Exclude.Paths)
            {
                if (MatchesGlob(normalizedPath, pathGlob))
                {
                    _logger.LogDebug("Excluded by path glob ({Glob}): {Path}", pathGlob, filePath);
                    return true;
                }
            }

            return false;
        }

        private bool MatchesWildcard(string input, string pattern)
        {
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
        }

        private bool MatchesGlob(string input, string glob)
        {
            // Simple glob matcher for **/something/** patterns
            var regexPattern = "^" + Regex.Escape(glob)
                .Replace("\\*\\*/", ".*")
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*")
                .Replace("\\?", ".") + "$";
            
            // Allow partial matching if glob doesn't start with ^ or **
            if (!glob.StartsWith("**"))
            {
                regexPattern = ".*" + regexPattern.Substring(1);
            }
            
            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
        }
    }
}
