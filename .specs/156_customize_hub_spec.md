# SPEC — 156 · Customize Hub unificado (Knowledge / Skills / MCPs)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `customize-hub` |
| Type | `Frontend` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #TBD · Epic #TBD |
| Scope | `src/Sandcastle.Client/` + `tests/` |

## 1. User Story

**As a** usuário, **I want** gerenciar Knowledge, Skills e MCPs num lugar
só, com escopo pessoal/organização claro, **so that** eu não precise
caçar cada recurso numa seção diferente de Settings — paridade com o
`/customize` do OpenWebUiCode (hub com tabs Knowledge|Skills|MCPs e
badges de escopo).

## 2. Contexto / escopo

Hoje: `/settings` (tab indexing + links), `/settings/skills`,
`/settings/mcp`, `/knowledge` — recursos espalhados e sem visão única do
"o que alimenta o agente". Proposta:

- Nova rota `/customize` (nav item "Customize" entre Conversations e
  Knowledge) com 3 tabs: **Knowledge**, **Skills**, **MCPs** — cada uma
  reusa os componentes existentes como painéis embedados.
- Cada item ganha badge de escopo `pessoal | organização` e origem
  (`repo | marketplace | manual`).
- `/settings/skills` e `/settings/mcp` viram redirects para
  `/customize?tab=skills|mcps` (deep-link preservado); `/knowledge`
  mantém a página full (tab Knowledge linka pra ela para ações pesadas).

Fora de escopo: editor de knowledge inline, novo backend, marketplace
browse (já tem rota própria).

## 3. Requisitos

- RF-001: rota `/customize` + `?tab=knowledge|skills|mcps` (default
  knowledge); nav item com ícone próprio; i18n ×8.
- RF-002: tabs renderizam os mesmos componentes das páginas atuais
  (extrair corpo de `SkillsPage`/`McpSettings`/`KnowledgePage` para
  components reusáveis `*Panel` sem duplicar lógica).
- RF-003: badges de escopo/origem em cada item das 3 listas.
- RF-004: redirects 301 client-side das rotas antigas de settings.
- RF-005: tab Knowledge resume (contagem, últimas sugestões da fila) +
  link "abrir knowledge" → `/knowledge`.

## 4. API Contract

Sem rotas novas; consome endpoints existentes de knowledge/skills/mcp.

## 5. Critérios de aceite

- **Dado** usuário em `/settings/mcp`, **quando** a rota carrega,
  **então** cai em `/customize?tab=mcps` mostrando o mesmo conteúdo.
- **Dado** skill pessoal e skill de org, **quando** lista,
  **então** badges distinguem os escopos.
- **Dado** deep-link `/customize?tab=skills`, **então** abre direto na
  tab skills e o back-button volta corretamente.

## 6. Task Plan

1. Extrair `SkillsPanel`/`McpPanel`/`KnowledgePanel` das páginas.
2. `CustomizePage` + rota + tabs + querystring + nav item.
3. Badges de escopo/origem nos DTOs client-side (metadata já existe?
   confirmar; se faltar `scope` no contrato, adicionar read-only).
4. Redirects das rotas antigas + testes bUnit + i18n ×8 + drift guards.

## 7. Deliverables

- `Pages/CustomizePage.razor` + 3 panels + redirects + i18n ×8 + testes

## 8. Referências

- OpenWebUiCode `Pages/Customize.razor` · `SettingsContent.razor` Sandcastle

## 9. Guardrails

- Zero duplicação de lógica: panels extraídos, páginas antigas delegam.
- Rotas antigas não quebram (redirect), bookmarks preservados.
- Sem mudança de contrato de escrita — CRUDs ficam onde estão.

## 10. DoD

- [ ] `/customize` com 3 tabs funcionais e deep-links.
- [ ] Redirects das rotas antigas verificados.
- [ ] badges de escopo visíveis; i18n ×8; drift guards verdes.
