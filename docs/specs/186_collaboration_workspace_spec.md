# SPEC — 186 · Workspace colaborativo (channels, notes, calendar)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `collaboration-workspace` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #560 · Epic #552 |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

<!-- Aprovada em 2026-11-27 — issue #560, Epic #552. -->

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** workspace colaborativo (channels, notes, calendar) com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenWebUiCode `ChannelPage.razor`, `Notes.razor`, `Calendar.razor`, `SPEC-20261002-channels-v2`, `notes-collab`, `automations-calendar`; OpenHands `task-list-tab`/automation activity patterns.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: channels de colaboração — DMs/grupos por org, threads, reactions, unread count e pins, com menções a conversations/runs.
- RF-002: notes colaborativas simples por workspace/org — editor markdown, autosave/versionamento básico, links para knowledge e conversations.
- RF-003: calendar/work schedule view consolidando automations, factories, runs e lembretes; filtros por repo/user/status.
- RF-004: notification feed unificado para mentions, attention inbox, automation failures, budget alerts e review requests; read/unread e deep links.
- RF-005: páginas `/channels`, `/notes`, `/calendar`, `/notifications` no shell novo, permissões org-scoped e i18n ×8.
- RF-006: migrations duais, endpoints, testes unit/integration/bUnit e auditoria de ações sensíveis.

## 5. API Contract

- CRUD `/api/v1/channels`, `/api/v1/notes`, `/api/v1/notifications`; `GET /api/v1/calendar/events`.

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
