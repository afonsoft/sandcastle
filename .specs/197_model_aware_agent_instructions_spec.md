# SPEC — 197 · Model-aware agent instructions

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `model-aware-agent-instructions` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #607 · Epic #603 |
| Source analysis | `docs/research/2026-11-autonomous-agent-parity-deep-dive.md` |

## 1. Problem

Cursor explicitly tunes instructions and tool use per frontier model. Sandcastle has model routing, profiles and prompt packs, but prompt construction is mostly model-agnostic. Different providers differ in tool-calling style, verbosity, planning format, cache behavior and safety constraints.

## 2. Requirements

- RF-001: add bounded model-instruction overlays keyed by provider/model family, versioned and auditable.
- RF-002: resolution order is safe default → provider family → exact model → agent profile override.
- RF-003: overlays can adjust guidance, tool-use style and output-contract reminders but cannot weaken security or permission rules.
- RF-004: expose effective overlay metadata in prompt diagnostics/events.
- RF-005: admin/profile editing UX or configuration documentation with validation.
- RF-006: unit/integration coverage for precedence, bounds, redaction and no-security-downgrade enforcement.

## 3. Acceptance criteria

- Two different model families can receive different bounded instruction overlays from the same base prompt.
- A profile override wins over model-family defaults while preserving hard security rules.
- Diagnostics reveal which overlay was applied without exposing secrets.
