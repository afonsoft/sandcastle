# SPEC — 190 · Operações de lifecycle enterprise cloud

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `enterprise-cloud-lifecycle` |
| Type | `Infra` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Completed` |
| Ticket | #564 · Epic #552 |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

<!-- Aprovada em 2026-11-27 — issue #564, Epic #552. -->

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** operações de lifecycle enterprise cloud com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenHands-Cloud templates `proactive-convo-clean`, `app-conversation-start-task-clean`, `integration-events`, `mcp-events`, `resend-sync`, `user-waitlist`, `enrich-user-interaction` — somente conceitos devido à licença Polyform; Sandcastle já possui auth/orgs/audit como base.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: org/workspace provisioning lifecycle — waitlist, invite, approval, onboarding status, default quotas/feature flags e duplicate email handling.
- RF-002: cleanup/reconciliation jobs — proactive conversation cleanup, start-task cleanup, abandoned sandbox/run cleanup e resync de notificações/integrações, configuráveis e auditados.
- RF-003: integration event pipeline confiável — fila/outbox, retries, dead-letter, replay e métricas para SCM/chat/ticket/webhooks.
- RF-004: user interaction enrichment opcional e privacy-aware para onboarding/activation analytics, sem coletar conteúdo sensível de prompts/código.
- RF-005: admin operations page com jobs, filas, dead letters, provisioning queue e runbook links; feature flags por ambiente.
- RF-006: migrations duais, endpoints, background workers idempotentes, testes unit/integration/bUnit e documentação operacional.

## 5. API Contract

- `GET/POST /api/v1/admin/lifecycle/jobs`, `/dead-letters/replay`, `/provisioning/*` (contrato a ser refinado na aprovação).

Contratos detalhados devem ser fixados na aprovação; novos endpoints exigem golden OpenAPI e authz consistente com org/user/admin.

## 6. Acceptance Criteria

- **Dado** a feature aprovada, **quando** implementada, **então** cada RF possui teste automatizado ou evidência explícita de cobertura.
- **Dado** usuário sem permissão, **quando** acessa recurso user/org/admin restrito, **então** recebe 401/403 e nenhum dado vaza.
- **Dado** deployment SQLite ou PostgreSQL, **quando** schema muda, **então** migrations duais aplicam sem drift.
- **Dado** UI alterada, **quando** renderizada em pt-BR, **então** fluxo principal funciona com i18n ×8 e drift guards verdes.

## 7. Task Plan

1. Refinar contrato e dependências; marcar integrações com SPECs 171–178.
2. Implementar backend/contratos + migrations duais quando necessário.
3. Implementar client/UX + i18n ×8.
4. Adicionar testes unit/integration/bUnit e golden OpenAPI quando API mudar.
5. Rodar build, format, drift guards, testes completos, PR, review, merge e arquivamento.

## 8. Organization Guardrails

- Não copiar código de OpenHands-Cloud (Polyform); implementar conceitos próprios.
- Nunca commitar secrets; secrets apenas por referência e com redaction.
- Branch/PR próprios; sem push direto em `main`; workflows GitHub não mudam sem revisão humana.
- Toda mudança de API/schema/UI segue golden OpenAPI, migrations duais, i18n ×8 e drift guards.

## 9. Definition of Done

- [ ] SPEC aprovada e issue/epic vinculados.
- [ ] RFs implementados com testes verdes.
- [ ] `dotnet format Sandcastle.slnx --verify-no-changes` limpo.
- [ ] Drift guards e golden OpenAPI verdes quando aplicável.
- [ ] Docs bilíngues atualizadas.
- [ ] SPEC movida para `docs/specs/` com `Status: Completed`.
