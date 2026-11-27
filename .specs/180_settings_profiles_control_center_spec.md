# SPEC — 180 · Settings center v2 + LLM/agent profiles

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `settings-profiles-control-center` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM · ASP.NET Core · EF Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | TBD — criar issue/epic após aprovação |
| Scope | `src/` + `tests/` + `docs/` conforme requisitos |

## 1. User Story

**Como** usuário/administrador do Sandcastle  
**quero** settings center v2 + llm/agent profiles com paridade conceitual ao OpenWebUiCode, OpenHands Cloud e Devin Cloud  
**para que** a plataforma fique mais próxima de um produto completo de cloud agents, sem depender de código proprietário/Polyform.

## 2. Scope

Em escopo: análise, contratos, implementação futura e testes descritos nos requisitos.  
Fora de escopo: copiar código/assets do OpenHands-Cloud (Polyform), implementar antes da aprovação da SPEC, ou quebrar compatibilidade dos fluxos já entregues nas SPECs 161–178.

## 3. Technical Context

Evidências analisadas: OpenWebUiCode `SPEC-20261017-settings-ux-redesign`, `SPEC-20261010-multi-provider-connections`; OpenHands `settings/agent-profiles/llm-profiles`.

Ler antes de implementar: `CLAUDE.md`, `.specs/README.md`, specs dependentes já arquivadas em `docs/specs/`, componentes/endpoints Sandcastle correspondentes e as fontes upstream citadas. OpenHands-Cloud é referência apenas conceitual por licença Polyform.

## 4. Requirements

- RF-001: redesenhar `/settings` em categorias com sticky save bar, estado dirty/saving/saved, mobile chips e a11y conforme `settings-ux-redesign` do OpenWebUiCode.
- RF-002: provider connections tipadas (OpenAI, Anthropic, Google, OpenAI-compatible, Ollama) com endpoints oficiais, teste de conexão, listagem nativa de modelos e segredos por referência.
- RF-003: LLM profiles nomeados/escopados (user/org) combinando provider, model, temperature, roles e fallback; seleção por conversa/automation.
- RF-004: agent profiles nomeados/escopados combinando prompt pack, tools permitidas, skills, mode defaults, runtime e model profile; CRUD + duplicate + delete seguro.
- RF-005: seções unificadas para MCP, integrations, skills, automations, secrets, runtime, verification e experimental features, respeitando permissões admin/user.
- RF-006: migrations duais, endpoints, golden OpenAPI, testes unit/integration/bUnit e i18n ×8.

## 5. API Contract

- CRUD `/api/v1/provider-connections`, `/api/v1/llm-profiles`, `/api/v1/agent-profiles`.
- `POST /api/v1/provider-connections/{id}/test` e `GET .../models`.

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
