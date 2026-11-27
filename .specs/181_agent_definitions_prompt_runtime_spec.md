# SPEC — 181 · Agent definitions, prompt packs e internal skills runtime

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-definitions-prompt-runtime` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #555 · Epic #552 |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

<!-- Aprovada em 2026-11-27 — issue #555, Epic #552. -->

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** agent definitions, prompt packs e internal skills runtime com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenWebUiCode `agents/*.agent.md`, `Infrastructure/SystemPrompt`, `prompts/advisor-system.md`, `prompts/smart-permission-judge.md`, `SPEC-20261014-agent-definitions`.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: suportar `*.agent.md` com frontmatter (name, description, tools, model/profile, skills, mode, permission preset) e precedência user/repo/org/system.
- RF-002: prompt packs versionados — system prompt base, harness rules, plan/build modes, advisor e smart permission judge; comparação de versão e rollback auditado.
- RF-003: catálogo de agentes acessível por API/UI e pela tool `agent_catalog`; `delegate_task` aceita referência a definition com escopo explícito.
- RF-004: internal skills runtime — skills declarativas com instruções, tools permitidas, triggers e testes de fumaça; auditoria de carregamento e uso (integra SPEC-168).
- RF-005: personas de agente por canal/conversa sem vazar prompts internos; renderização segura de instruções e redaction de secrets.
- RF-006: testes unit de parser/precedência, integração de delegate/catalog, bUnit do catálogo e golden OpenAPI.

## 5. API Contract

- CRUD/read `/api/v1/agent-definitions`, `/api/v1/prompt-packs`; `POST /api/v1/agent-definitions/{id}/validate`.

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
