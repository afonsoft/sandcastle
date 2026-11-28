# Navigation shell (SPEC-179)

Devin Web-inspired product shell for conversations and cloud operations.

## Contextual topbar

On desktop, the topbar shows breadcrumbs per area (`/conversations`,
`/automations`, `/workspace`, `/settings`, `/extensions`, `/factories`),
operational counters and global actions. On mobile, navigation keeps
using the existing compact topbar.

## Command center

`Ctrl+K` (or `/`) opens the command palette. Besides recent conversations
and quick actions, it indexes product areas: automations, knowledge,
workspace, extensions, factories, MCP/skills and settings.

## Session list

The sidebar keeps live conversation status/attention and shows aggregated
session cost when `TokenEvent.cost_usd` exists. The conversation detail
pane remains resizable per conversation.

## Focus mode

The focus button in the topbar hides the sidebar and expands the main
pane; the preference persists in `sessionStorage` (`sc.shell.focus`).

## Foundation routes

- `/extensions` — foundation entry for the unified extensions hub;
  the full implementation belongs to SPEC-187.
- `/factories` — foundation entry for workflow factories/delivery;
  the full implementation belongs to SPEC-183.

## UX telemetry

`POST /api/v1/ux-events` receives navigation events (`route_navigation`)
with render duration. The backend reduces the route to a product area
before logging — IDs, titles and conversation content are never written.
`GET /api/v1/navigation/summary` feeds badges with attention, running,
automation, factory and extension counts.
