# SPEC — 182 · Paridade de tools do agente v2

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-tool-parity-v2` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Completed` |
| Ticket | #556 · Epic #552 |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

<!-- Aprovada em 2026-11-27 — issue #556, Epic #552. Entregue via PR #572. -->

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** paridade de tools do agente v2 com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenWebUiCode `ChatTools/Tools/*.cs` (66 tools versus 55 no Sandcastle), especialmente `AgentValidateBuiltinTool.cs`, `AgentLoopTools.cs`, browser tools e media tools; SPECs Sandcastle 174/176 como complemento, não substituto.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: implementar ferramentas ausentes de alto valor: `agent_validate`, `instant_context`, `memory_learn`, `reminder`, `code_interpreter`, `plan_exit`, `mode_switch` e `unseen_messages`.
- RF-002: browser tool granular (`browser_navigate/observe/act/screenshot/close`) com visual reasoning, isolamento de sessão e screenshots no event stream, evoluindo o `browser` monolítico atual sem quebrar compatibilidade.
- RF-003: context ops complementares à SPEC-176: `context_compact`, `context_recovery`, `extend_context`, `context_usage` com payload operacional consistente.
- RF-004: media generation opcional (`generate_image`, `generate_video`) atrás de feature flag/provider configurado; nunca falhar silenciosamente.
- RF-005: normalizar aliases de paridade (`shell_exec`→bash, `fetch_url`→web_fetch, `todo_write`→task_tracker) sem expor duplicidade confusa no catálogo.
- RF-006: testes de schema/dispatch/permissions, integração no sandbox e golden MCP/tools quando aplicável.

## 5. API Contract

- Sem novos endpoints REST primários; contrato principal é o catálogo de tools do agente/MCP.

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
