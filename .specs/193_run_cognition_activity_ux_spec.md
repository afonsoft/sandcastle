# SPEC — 193 · Run cognition and activity UX parity

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `run-cognition-activity-ux` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #594 · Epic #591 |
| Source analysis | `docs/research/2026-11-openwebui-chat-main-comparison.md` |

## 1. Problem

OpenWebUiCode main improved the agent activity loop with explicit goal/wait/memory tools, a canonical analyzing phase, advisor severities, robust tool summaries, diff preservation and mobile activity polish. Sandcastle has parts of this surface (think, task tracker, event phases, insights) but lacks a coherent parity contract.

## 2. Requirements

- RF-001: add explicit `goal` tool recording current run objective and surface it in activity state.
- RF-002: add bounded `wait` tool for elapsed time/background jobs without blocking the executor indefinitely.
- RF-003: add `memory_reflect` producing a bounded digest of relevant memory/knowledge entries with provenance.
- RF-004: define and emit one canonical `analyzing` phase before the first generating phase in both tool-loop and plain-completion paths.
- RF-005: normalize advisor output severity (`nit`, `concern`, `blocker`) into SSE/event payloads and render it in activity.
- RF-006: tool output previews interpolate summaries, preserve diff lines, and restore persisted tool names after reload.
- RF-007: mobile activity/insights layout polish consistent with SPEC-177, i18n ×8 and drift guards.
- RF-008: unit/integration/bUnit coverage for event ordering, parsing, rendering and persistence.

## 3. Acceptance criteria

- A run shows exactly one analyzing phase before generation and exposes goal/wait/memory activity.
- Advisor severities are machine-readable and visible in the UI.
- Reloaded conversations preserve readable tool identity and output previews.
