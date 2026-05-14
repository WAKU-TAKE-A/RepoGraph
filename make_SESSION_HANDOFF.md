You are generating a handoff document for another AI system.

The goal is NOT to create a human meeting summary.

The goal is to transfer:
- reasoning state
- architectural context
- failed attempts
- important constraints
- current decisions
- unresolved risks
- next recommended actions

The next AI must be able to continue work efficiently without repeating failed exploration.

IMPORTANT:
Focus on:
- why decisions were made
- what was rejected
- what partially worked
- what remains uncertain
- current confidence levels

Avoid verbose chronological logs.

Prefer compact structured knowledge.

Generate the document using the following structure:

# Current Objective

# Current System State

# Confirmed Findings

# Important Constraints

# Decisions Made

For each decision include:
- decision
- reason
- alternatives rejected
- confidence level

# Failed Attempts

For each failed attempt include:
- what was attempted
- why it failed
- whether retry is recommended

# Partial Successes

# Unresolved Problems

# Technical Risks

# Recommended Next Actions

# Suggested Investigation Areas

# Important Files / Components

# Important Commands / Scripts

# Known Performance Issues

# Known Stability Issues

# Context That Future AI Must NOT Lose

Prefer concise technical summaries over prose.

Do NOT generate motivational text.

Do NOT generate project-management style reporting.

Optimize for machine continuation efficiency.