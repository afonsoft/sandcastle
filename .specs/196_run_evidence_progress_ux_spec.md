# SPEC — 196 · Run evidence and unified progress UX

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `run-evidence-progress-ux` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #606 · Epic #603 |
| Source analysis | `docs/research/2026-11-autonomous-agent-parity-deep-dive.md` |

## 1. Problem

Devin exposes a unified Progress surface combining shell, IDE and browser evidence; Cursor Cloud Agents attach screenshots, videos and logs as proof. Sandcastle already streams rich events and has terminal/browser/desktop tabs, but operators must reconstruct the run from separate surfaces and evidence artifacts are not first-class.

## 2. Requirements

- RF-001: define durable run evidence artifact records for screenshots, videos, logs, diffs and reports with type, provenance, content type, size and redaction metadata.
- RF-002: add upload/list/download endpoints with size/type limits and org/user authorization.
- RF-003: browser/desktop/terminal evidence can be attached to a run without embedding large binary data in event ledger payloads.
- RF-004: unified Progress tab summarizes phases, shell commands, tool outputs, files changed and evidence links with copyable command/output previews.
- RF-005: PR creation can reference selected evidence artifacts when available, without committing binaries or secrets.
- RF-006: mobile responsive polish, i18n ×8, drift guards, unit/integration/bUnit coverage.

## 3. Acceptance criteria

- A run can store and retrieve an evidence artifact through the API with correct authorization and bounds.
- Progress tab shows chronological command/tool/phase context plus evidence links.
- Reloaded conversations preserve artifact references and readable previews.
