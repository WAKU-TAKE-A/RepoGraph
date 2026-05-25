using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Probe.Services.Analysis.Dsl
{
    public class DslRule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("scope")]
        public DslScope Scope { get; set; } = new();

        [JsonPropertyName("match")]
        public DslMatch Match { get; set; } = new();

        [JsonPropertyName("bind")]
        public Dictionary<string, string> Bind { get; set; } = new();

        [JsonPropertyName("resolve")]
        public DslResolve Resolve { get; set; } = new();

        [JsonPropertyName("emit")]
        public DslEmit Emit { get; set; } = new();
    }

    public class DslScope
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("file_glob")]
        public List<string>? FileGlob { get; set; }
    }

    public class DslMatch
    {
        [JsonPropertyName("where")]
        public List<DslCondition> Where { get; set; } = new();
    }

    public class DslCondition
    {
        [JsonPropertyName("field")]
        public string Field { get; set; } = string.Empty;

        [JsonPropertyName("op")]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public object? Value { get; set; }
    }

    public class DslResolve
    {
        [JsonPropertyName("target")]
        public DslResolveTarget Target { get; set; } = new();
    }

    public class DslResolveTarget
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("where")]
        public List<DslCondition> Where { get; set; } = new();
    }

    public class DslEmit
    {
        [JsonPropertyName("edge_kind")]
        public string EdgeKind { get; set; } = string.Empty;

        [JsonPropertyName("call_type")]
        public string CallType { get; set; } = string.Empty;

        [JsonPropertyName("rule_id")]
        public string RuleId { get; set; } = string.Empty;

        [JsonPropertyName("rule_family")]
        public string RuleFamily { get; set; } = string.Empty;

        [JsonPropertyName("rule_mode")]
        public string RuleMode { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public string Confidence { get; set; } = string.Empty;
    }
}
