# SPEC — 184 · Control plane de runtimes cloud

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `cloud-runtime-control-plane` |
| Type | `Infra` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #558 · Epic #552 |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

<!-- Aprovada em 2026-11-27 — issue #558, Epic #552. -->

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** control plane de runtimes cloud com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenHands-Cloud `docs/ARCHITECTURE.md` (Runtime API, warm runtimes, image loader) e templates Helm relacionados — somente conceitos, porque o repo é Polyform; Sandcastle `runtime-api`, outposts e SPEC-175.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: control plane de runtimes/sandboxes — fleet view com pods/sandboxes por estado, image, node/outpost, warm pool, age, owner/conversation e health.
- RF-002: lifecycle operations admin — drain, terminate, recycle stale runtime, resize warm pool, image preload/pull status e rollback seguro com audit.
- RF-003: runtime inventory orientado a capacity: CPU/memória reservada versus uso, concurrency limits, queue depth, provisioning latency e failure rate.
- RF-004: integração com runtime-api/k8s quando disponível e fallback Docker local, mantendo conceitos do OpenHands-Cloud sem copiar código/chart Polyform.
- RF-005: página admin `/admin/runtime` + endpoint operacional e alertas básicos (degraded pool, image pull error, sandbox watchdog da SPEC-175).
- RF-006: testes unit de inventory/policy, integration admin API, bUnit fleet view e docs operacionais.

## 5. API Contract

- `GET /api/v1/admin/runtime/fleet`, `/pools`, `/images`; `POST .../drain`, `.../terminate`, `.../resize`.

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
