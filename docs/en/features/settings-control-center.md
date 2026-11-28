# Settings control center (SPEC-180)

Settings now work as a profile-oriented control center, inspired by the
OpenWebUI settings UX and cloud agent products.

## Settings shell

`/settings` groups workspace, operations and account settings in the left
rail. Mobile keeps the same sections as horizontal chips. The general form
tracks dirty/saving/saved state in the agent/LLM form and the **Sections** tab links the unified
operational surfaces: MCP, extensions, skills, automations, secrets,
runtime/outposts, verification/scans and admin-only experimental/SCM areas.

## Provider connections

`Settings → Profiles` manages typed provider connections for OpenAI,
Anthropic, Google, OpenAI-compatible endpoints and Ollama. Connections use
official provider endpoints by default, can be tested, and list native
provider models. Raw secret values are never returned by the API: secrets
are stored through `ISecretStore` and responses expose only the secret
reference.

Endpoints:

- `GET/POST/PUT/DELETE /api/v1/provider-connections`
- `POST /api/v1/provider-connections/{id}/test`
- `GET /api/v1/provider-connections/{id}/models`

## LLM profiles

Named LLM profiles combine provider connection, primary model,
temperature, per-role model overrides and ordered fallback models. They
can be scoped to a user or org (org writes require owner/admin).

At runtime, a selected profile overlays model routing: role-specific
models win over the legacy pass-through map, fallback models become an
ordered route plan, and temperature is sent to the LLM provider. When the
profile references a provider connection, that connection supplies the
endpoint for the run and its stored secret when the provider requires one.

Endpoints: `GET/POST/PUT/DELETE /api/v1/llm-profiles`.

## Agent profiles

Named agent profiles combine prompt pack instructions, allowed tools,
skills, agent type, mode defaults, runtime profile and an optional LLM
profile. They support create, update, duplicate and safe delete. Deletes
return `409` while conversations, automations or dependent profiles still
reference the profile.

When selected on conversation or automation creation, Sandcastle
materializes mode/runtime/agent type/model defaults and converts allowed
tools into conversation permission rules. Prompt-pack instructions and the
profile skill subset are applied when the system prompt is rebuilt.

Endpoints: `GET/POST/PUT/DELETE /api/v1/agent-profiles` plus
`POST /api/v1/agent-profiles/{id}/duplicate`.

## Conversation and automation selection

`POST /api/v1/app-conversations` and automation definitions accept
`llm_profile_id` and `agent_profile_id`; the new-conversation and
new-automation forms expose the same selectors. Invisible or missing profiles are
rejected with `400`; org/user visibility is enforced server-side. Existing
conversations without profile IDs keep legacy behavior.

Schema changes are covered by dual SQLite/PostgreSQL migrations.
