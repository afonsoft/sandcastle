# SPEC — 183 · Workflows, factories e autonomous delivery

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `workflow-factories-autonomous-delivery` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | TBD — criar issue/epic após aprovação |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** workflows, factories e autonomous delivery com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenHands `factories-list.tsx`, `automation-templates.tsx`, `automation-events.tsx`, `AutomationService`; OpenWebUiCode `SPEC-20261124-named-workflows`, `SPEC-20261124-autonomous-delivery`, `SPEC-20261123-pipeline-fanout`.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: workflows nomeados e versionados compostos por steps agent/script/approval, com inputs tipados, retry policy e runs auditáveis.
- RF-002: factories como agrupadores de automações (CRUD, rename/delete, listagem por factory, templates), conforme OpenHands `use-factories`/`AutomationService`.
- RF-003: automation templates e event center — templates recomendados, eventos recentes, filtros por factory/status/repo e deep links para runs.
- RF-004: autonomous delivery loop opt-in: plan→implement→validate→PR→CI watch→review-fix→merge-ready, com hard gates humanos e orçamento.
- RF-005: UI `/factories`, `/automations/templates`, `/automations/events` e editor de workflow; mobile básico e i18n ×8.
- RF-006: migrations duais, endpoints, testes unit/integration/bUnit, golden OpenAPI e audit events.

## 5. API Contract

- CRUD `/api/v1/factories`; CRUD/run `/api/v1/workflows`; `GET /api/v1/automation-events`.

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
