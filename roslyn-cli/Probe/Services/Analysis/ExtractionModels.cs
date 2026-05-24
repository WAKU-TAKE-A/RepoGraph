using System.Collections.Generic;

namespace Probe.Services.Analysis
{
    public class SymbolData
    {
        public string Id { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Fqn { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Namespace { get; set; }
        public string? ContainingType { get; set; }
        public string Accessibility { get; set; } = "";
        public bool IsStatic { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsAsync { get; set; }
        public bool IsPartial { get; set; }
        public bool IsGeneric { get; set; }
        public bool IsExtensionMethod { get; set; }
        public bool IsDisposable { get; set; }
        public bool IsVolatile { get; set; }
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public int Loc { get; set; }
        public int ParameterCount { get; set; }
        public string? ReturnType { get; set; }
        public bool HasCallback { get; set; }
        // Thread boundary flags
        public bool HasUiDispatch { get; set; }
        public bool HasTaskSpawn { get; set; }
        public bool HasBackgroundWorker { get; set; }
        public bool HasDoEvents { get; set; }
        public bool HasLock { get; set; }
        public bool HasThreadStart { get; set; }
        public bool HasBlockingWait { get; set; }
        public int FanIn { get; set; }
    }

    public class MethodCallData
    {
        public string CallerId { get; set; } = "";
        public string CalleeId { get; set; } = "";
        public int CallCount { get; set; }
        public string CallType { get; set; } = "calls"; // "calls", "event_subscribe", "event_unsubscribe"
        public string? RuleId { get; set; }
        public string? RuleFamily { get; set; }
        public string? RuleMode { get; set; }
    }

    public class FieldAccessData
    {
        public string AccessorFqn { get; set; } = "";
        public string TargetFqn { get; set; } = "";
        public string AccessKind { get; set; } = "read"; // "read", "write", "read_write"
        public bool IsExternal { get; set; }
    }

    public class ProjectDependencyData
    {
        public string SourceProjectId { get; set; } = "";
        public string TargetProjectId { get; set; } = "";
    }

    public class InheritanceData
    {
        public string DerivedId { get; set; } = "";
        public string BaseId { get; set; } = "";
        public string Kind { get; set; } = "";
    }

    public class TypeDependencyData
    {
        public string SourceFqn { get; set; } = "";
        public string TargetFqn { get; set; } = "";
        public string Kind { get; set; } = "type_usage";
        public string? RuleId { get; set; }
        public string? RuleFamily { get; set; }
        public string? RuleMode { get; set; }
    }

    public class ExtractionResult
    {
        public List<SymbolData> Symbols { get; set; } = new List<SymbolData>();
        public List<MethodCallData> MethodCalls { get; set; } = new List<MethodCallData>();
        public List<InheritanceData> Inheritances { get; set; } = new List<InheritanceData>();
        public List<FieldAccessData> FieldAccesses { get; set; } = new List<FieldAccessData>();
        public List<TypeDependencyData> TypeDependencies { get; set; } = new List<TypeDependencyData>();
    }
}
