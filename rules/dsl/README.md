# This directory contains production DSL heuristic rule files (*.json).
#
# Rules in this directory are NOT loaded by default.
# To enable DSL candidate loading, set in analyzer.yml:
#
#   analysis:
#     enableDslCandidates: true
#     dslRulesDirectory: ""   # leave empty to use this directory automatically
#
# Each rule file must contain a single DslRule JSON object.
# These rules emit Candidate edges only. They do not create HardEdge relationships.
#
# Initial shipped rules are intentionally limited to patterns the current generic
# adapters can support without framework-specific C# logic:
#
# - xaml.event_handlers: direct XAML event attributes to code-behind methods
# - di.service_provider_generic: GetService<T>/GetRequiredService<T> candidates
# - di.autofac_resolve_generic: Autofac Resolve<T> candidates
#
# See roslyn-cli/Probe.Dsl.Tests/Fixtures/ for small representative schema examples.
