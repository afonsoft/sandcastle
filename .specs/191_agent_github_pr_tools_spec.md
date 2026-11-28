# SPEC — 191 · Agent GitHub PR tools

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-github-pr-tools` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Source analysis | `docs/research/2026-11-openwebui-chat-main-comparison.md` |

## 1. Problem

Sandcastle agents can create PRs but cannot inspect the PR loop natively: list open PRs, view details/checks/reviews, read diffs, or comment. OpenWebUiCode main now exposes this workflow through `gh_pulls`, `gh_view`, `gh_diff` and `gh_comment`; equivalent capability is central to Devin-like autonomous delivery.

## 2. Requirements

- RF-001: add read-only `github_list_pull_requests` supporting state, limit, repository binding and redacted output.
- RF-002: add read-only `github_view_pull_request` returning metadata, body excerpt, checks/review state and changed-file summary.
- RF-003: add read-only `github_read_pull_request_diff` with file/path filtering, patch truncation and binary-file markers.
- RF-004: add risky `github_comment_pull_request` behind the existing permission/risk model, with idempotency metadata and audit events.
- RF-005: tool activity labels/i18n ×8 and persisted tool names in conversation history.
- RF-006: unit/integration coverage for permissions, truncation, repository scoping and error paths; no secrets or tokens in outputs.

## 3. Acceptance criteria

- Agent can inspect a linked repository PR loop without shelling to `gh`.
- Comment creation requires the same risky-tool approval path as mutating tools.
- Output is deterministic, bounded and citation-friendly (`owner/repo#number`, file path, line/hunk where applicable).
