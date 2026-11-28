# Enterprise lifecycle operations

## English

SPEC-190 adds an enterprise lifecycle operations surface:

- `/admin/lifecycle` shows the provisioning queue, durable integration outbox/dead letters and audited operational job runs.
- Provisioning supports waitlist creation, duplicate email handling, approval and onboarding markers, plus default quota/feature-flag JSON metadata.
- Integration events are persisted in an outbox with bounded retries, exponential backoff, dead-letter state and operator replay. Delivery targets must be absolute HTTPS URLs and pass the SSRF guard.
- Operational jobs expose audited reconciliation entry points for proactive conversations, start tasks, abandoned sandboxes and integration resync. Existing specialized workers remain the destructive cleanup owners; these admin runs provide operator visibility/audit and resync metrics.
- `POST /api/v1/lifecycle/interactions` accepts only allowlisted privacy-safe onboarding/activation event names and never accepts prompt/code/content payloads.
- `Lifecycle:Enabled=false` disables the background outbox worker; `Lifecycle:OutboxIntervalSeconds` and `Lifecycle:OutboxBatch` tune processing.
- SQLite and PostgreSQL migrations cover provisioning requests, outbox events and job runs.

## Português

A SPEC-190 adiciona uma superfície de operações de lifecycle enterprise:

- `/admin/lifecycle` mostra fila de provisioning, outbox durável/dead letters de integrações e runs auditados de jobs operacionais.
- Provisioning suporta waitlist, deduplicação de email, aprovação e marcação de onboarding, além de metadata JSON de quotas/feature flags default.
- Eventos de integração são persistidos em outbox com retries limitados, backoff exponencial, dead-letter e replay operacional. Targets precisam ser URLs HTTPS absolutas e passar pelo SSRF guard.
- Jobs operacionais expõem entrypoints auditados de reconciliação para conversas proativas, start tasks, sandboxes abandonados e resync de integrações. Workers especializados existentes continuam donos do cleanup destrutivo; esses runs admin fornecem visibilidade/auditoria e métricas de resync.
- `POST /api/v1/lifecycle/interactions` aceita apenas eventos allowlisted e privacy-safe de onboarding/activation, sem payload de prompt/código/conteúdo.
- `Lifecycle:Enabled=false` desativa o worker de outbox; `Lifecycle:OutboxIntervalSeconds` e `Lifecycle:OutboxBatch` ajustam o processamento.
- Migrations SQLite e PostgreSQL cobrem provisioning, outbox e job runs.
