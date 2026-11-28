# SPEC — 179 · Shell de navegação estilo Devin + command center

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `devin-navigation-shell` |
| Type | `Frontend` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `In implementation` |
| Ticket | #553 · Epic #552 |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

<!-- Aprovada em 2026-11-27 — issue #553, Epic #552. -->

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** shell de navegação estilo devin + command center com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenHands `src/routes/root-layout.tsx`, `settings-navigation.tsx`; OpenWebUiCode `Layout/MainLayout.razor`; Devin Web como referência visual externa.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: shell em três zonas — sidebar de navegação/workspaces, topbar contextual e área principal com panes redimensionáveis, inspirado no Devin Web e no `root-layout.tsx` do OpenHands.
- RF-002: command center global (palette) unificando conversas, automações, factories, settings, knowledge, MCP/extensions e ações rápidas, com busca fuzzy e keyboard-first.
- RF-003: navegação por contexto — rotas `/conversations`, `/automations`, `/factories`, `/workspace`, `/settings`, `/extensions`; breadcrumbs e estado de navegação preservado por aba.
- RF-004: densidade/layout Devin-like — lista de sessões com status/attention/custo, painel de detalhe ao lado, modo foco e colapso da sidebar; desktop primeiro, mobile delegado à SPEC-177.
- RF-005: telemetria de UX — eventos de navegação e tempo de carregamento de rota sem PII.
- RF-006: testes bUnit de rotas/shell/palette + drift guards CSS/i18n ×8.

## 5. API Contract

- `GET /api/v1/navigation/summary` opcional: contadores de attention, running sessions, automations e factories para badges da sidebar.

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
