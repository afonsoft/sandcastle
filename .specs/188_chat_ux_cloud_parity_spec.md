# SPEC — 188 · Chat UX cloud parity v2

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `chat-ux-cloud-parity` |
| Type | `Frontend` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | TBD — criar issue/epic após aprovação |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** chat ux cloud parity v2 com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenWebUiCode specs `chat-activity-feed`, `chat-branching`, `chat-detached-runs`, `chat-ui-polish`, `pr-ci-panel`, `attachments-v2`; OpenHands `conversation.tsx`, `commits-tab.tsx`, `browser-tab.tsx`; Devin Web como referência de UX.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: activity feed rico da conversa — run hierarchy, tool streaming, thinking stream, lifecycle meta, duration/cost e filtros por tipo, mantendo performance em conversas longas.
- RF-002: chat branching e detached runs — criar branch a partir de mensagem/checkpoint, comparar branches e anexar run paralela sem bloquear a conversa principal.
- RF-003: PR/CI panel contextual — status de checks, review comments, preview links, commits e ações (retry, open PR, fix failures) dentro da conversa.
- RF-004: markdown/source view, codeblock toolbar, attachments v2 e composer sliders/MCP ids conforme gaps do OpenWebUiCode, preservando SPEC-45/170.
- RF-005: virtualização/performance da janela de chat e a11y keyboard/focus/landmarks.
- RF-006: testes bUnit, integration de APIs complementares, drift guards CSS/i18n ×8 e medição de render performance.

## 5. API Contract

- `GET /api/v1/conversations/{id}/branches`, `POST .../branch`, `GET .../pr-status` (contratos podem ser refinados na aprovação).

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
