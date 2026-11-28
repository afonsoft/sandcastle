# SPEC — 195 · Project coordinator workspaces

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `project-coordinator-workspaces` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #605 · Epic #603 |
| Source analysis | `docs/research/2026-11-autonomous-agent-parity-deep-dive.md` |

## 1. Problem

Cursor Projects and current OpenHands expose a durable project workspace: a coordinator conversation plans and delegates to worker conversations while shared project memory/files preserve context. Sandcastle has subagents, channels, factories and multi-repo support, but no first-class project aggregate tying coordinator, workers, board state and shared instructions together.

## 2. Requirements

- RF-001: add project entity/model with name, description, repositories, coordinator conversation id, status, timestamps and org/user scoping.
- RF-002: add API endpoints to create/list/get/update/archive projects and attach/detach worker conversations.
- RF-003: project creation can start or link a coordinator conversation using a selected agent profile.
- RF-004: shared project memory/files are bounded, auditable and available to coordinator/worker prompts.
- RF-005: UI project list/detail with coordinator state, worker conversations, shared notes/files and navigation into conversations.
- RF-006: delegation policy explains coordinator/worker boundaries; workers do the implementation, coordinator plans/reviews/steers.
- RF-007: migrations dual SQLite/PostgreSQL, golden OpenAPI, i18n ×8, unit/integration/bUnit coverage.

## 3. Acceptance criteria

- User can create a project, open its coordinator and inspect linked worker conversations.
- Project shared notes are durable and visible in prompt context for linked runs.
- Project archive hides active work without deleting conversations or events.
