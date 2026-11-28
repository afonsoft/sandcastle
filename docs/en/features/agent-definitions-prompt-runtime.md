# Agent definitions and prompt runtime (SPEC-181)

`Settings → Profiles` now includes the agent-definition catalog and the
versioned prompt packs consumed by the agent runtime.

## Agent definitions

Declarative definitions keep the SPEC-95 `*.agent.md` format, now with
extended frontmatter: `name`, `description`, `tools`, `model`,
`model_profile`, `skills`, `mode` and `permission_preset`. In addition to
builtin/embedded, user-file and workspace layers, persisted database
definitions join the user/org precedence pipeline:

`builtin/system < org < user < workspace`.

`POST /api/v1/agent-definitions/{id}/validate` validates required fields,
allowed modes and known tools. The `task` tool accepts an explicit
`agent_scope` (`builtin`, `system`, `user`, `org` or `workspace`) to
delegate to a definition in the intended scope; unknown names return an
error with the valid catalog.

Endpoints: `GET/POST/PUT/DELETE /api/v1/agent-definitions` and
`POST /api/v1/agent-definitions/{id}/validate`.

## Versioned prompt packs

Prompt packs group base prompt, harness rules and plan-mode, build-mode,
advisor and permission-judge instructions. Updates never rewrite the old
row: they create a new active row with an incremented version. Line-level
diff is available through `compare`, and rollback copies an older version
into a new audited version.

Endpoints: `GET/POST/PUT/DELETE /api/v1/prompt-packs`,
`GET /api/v1/prompt-packs/{packId}/versions`,
`GET /api/v1/prompt-packs/{packId}/compare?from=1&to=2` and
`POST /api/v1/prompt-packs/{packId}/rollback/{version}`.

## Runtime, personas and safety

Agent profiles can reference a persisted pack with
`pack:<pack-name>`; when the system prompt is rebuilt, Sandcastle injects
the active pack sections into the conversation, preserving mode- and
conversation-specific behavior. Legacy values keep being treated as direct
instructions.

Instructions returned by the API are redacted for secret references
(`{{secret:...}}`) and other sensitive values. Skill loading/usage plus
CRUD, update and rollback operations are audited; unauthorized users
receive 401/403 without data from other personas.

Schema changes are covered by dual SQLite/PostgreSQL migrations and the
updated golden OpenAPI contract.
