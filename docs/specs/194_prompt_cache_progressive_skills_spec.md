# SPEC — 194 · Prompt cache and progressive skill context

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `prompt-cache-progressive-skills` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #604 · Epic #603 |
| Source analysis | `docs/research/2026-11-autonomous-agent-parity-deep-dive.md` |

## 1. Problem

Sandcastle has ordered prompt contributors, but all sections are effectively concatenated without cache-tier semantics. Skills are either injected in full or unavailable, lacking progressive name/description disclosure and path-triggered activation. OpenWebUiCode/OpenHands show lower-token patterns: static/dynamic prompt split, catalog-first skills, autoinvoke only when explicit, and path-triggered rules.

## 2. Requirements

- RF-001: add a prompt cache tier contract (`static`/`dynamic`) to contributors and compose stable sections before dynamic sections with explicit ordering.
- RF-002: expose cache composition metadata in prompt diagnostics/events without changing provider wire behavior unsafely.
- RF-003: add `path` skill trigger matching workspace-relative file paths touched or referenced by the run.
- RF-004: advertise non-injected skills as a bounded name/description catalog and reserve full injection for matching triggers or explicit invocation.
- RF-005: preserve existing Always/Keyword/Task behavior, allowed-tools enforcement and repo/org/user precedence.
- RF-006: unit/integration coverage for ordering, cache-tier separation, path matching, catalog bounds and regression of existing triggers.

## 3. Acceptance criteria

- A prompt with static and dynamic contributors orders all static content before dynamic content and records cache metadata.
- A path-triggered skill is injected only when a matching workspace path is present.
- Non-triggered skills appear as compact catalog entries rather than full bodies.
- Existing SPEC-168 allowed-tools behavior remains green.
