# Workflow factories and autonomous delivery

SPEC-183 adds factories, versioned workflows, automation templates, a unified event center, and an opt-in autonomous delivery loop.

- Factories group automations and workflows. Deleting a factory unassigns members without deleting them.
- Workflows are named/versioned JSON definitions with typed inputs, retry policy, and auditable runs.
- Steps are `agent`, `script`, or `approval`. Approval steps are hard human gates.
- The autonomous delivery preset is plan → implement/validate → PR/CI/review-fix → merge-ready. Agents are explicitly instructed not to merge or bypass review.
- UI routes: `/factories`, `/workflows/{id}`, `/automations/templates`, `/automations/events`.

## API

- `GET/POST/PATCH/DELETE /api/v1/factories`
- `GET /api/v1/factories/{id}/automations`
- `GET/POST/PATCH/DELETE /api/v1/workflows`
- `POST/GET /api/v1/workflows/{id}/runs`
- `GET /api/v1/workflows/runs/{runId}`
- `POST /api/v1/workflows/runs/{runId}/approval`
- `GET /api/v1/automation-templates`
- `GET /api/v1/automation-events`

# Factories de workflows e entrega autônoma

A SPEC-183 adiciona factories, workflows versionados, templates de automação, event center unificado e um loop opt-in de entrega autônoma.

- Factories agrupam automações e workflows. Excluir uma factory desvincula membros sem apagá-los.
- Workflows são definições JSON nomeadas/versionadas com inputs tipados, retry policy e runs auditáveis.
- Steps são `agent`, `script` ou `approval`. Steps de aprovação são gates humanos rígidos.
- O preset autônomo é plan → implement/validate → PR/CI/review-fix → merge-ready. Agentes são instruídos explicitamente a não mergear nem contornar revisão.
- Rotas: `/factories`, `/workflows/{id}`, `/automations/templates`, `/automations/events`.
