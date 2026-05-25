namespace Probe.Services.Analysis.Dsl
{
    public sealed record DslDiagnostic(
        string RuleId,
        string Severity,
        string Code,
        string Message
    );
}
