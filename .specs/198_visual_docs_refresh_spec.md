# SPEC — 198 · Visual documentation refresh

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `visual-docs-refresh` |
| Type | `Docs` |
| Stack | `Documentation · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #608 · Epic #603 |
| Source analysis | `docs/research/2026-11-autonomous-agent-parity-deep-dive.md` |

## 1. Problem

README and auxiliary docs describe many capabilities, but the visual evidence is stale relative to the current Devin/OpenHands-inspired shell, conversation workspace, settings, projects/progress surfaces and admin runtime controls.

## 2. Requirements

- RF-001: run Sandcastle locally after implementing the parity round.
- RF-002: capture representative desktop/mobile screenshots of home, conversation activity/progress, settings, projects/evidence surfaces where available.
- RF-003: store optimized screenshots in repository docs assets without secrets or user-sensitive data.
- RF-004: update README and auxiliary docs in English and Portuguese with current visual references and feature descriptions.
- RF-005: verify image links render and documentation guards/build remain green.

## 3. Acceptance criteria

- README shows current UI screenshots rather than stale or absent visuals.
- Docs describe the implemented parity surfaces accurately.
- No secrets, tokens, private repositories or sensitive conversation content appear in screenshots.
