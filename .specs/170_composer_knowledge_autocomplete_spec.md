# SPEC — 170 · Composer knowledge autocomplete (menções ricas)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `composer-knowledge-autocomplete` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #492 · Issue #502 |
| Scope | `src/Sandcastle.Client` + `tests/` |

## 1. User Story

**As a** usuário escrevendo no composer, **I want** autocomplete de
menções (`@`) para arquivos, conversas, skills, automations e knowledge
**so that** referencio contexto sem digitar ids — paridade com as
menções ricas do OpenWebUiCode (`27aeb87` mobile popups incluso).

## 2. Contexto / escopo

`ChatComposer.razor` hoje tem menções apenas de **file paths** (grep no
workspace). Faltam fontes: conversas recentes, skills instaladas,
automations, knowledge docs; preview rico no popup; navegação por
teclado; suporte mobile (popover não corta em telas pequenas).

Faltam:

- Fontes de menção unificadas (`IMentionProvider` por tipo:
  file/conversation/skill/automation/knowledge) com debounce e cache.
- Popup rico: ícone/tipo, título, snippet, badge de origem.
- Teclado: ↑↓ navigate, Enter select, Esc close; mouse hover/click.
- Mobile: popup ancorado com scroll, não overflow (paridade `27aeb87`).
- Serialização da menção no texto enviado (`@[label](type:id)` ou
  token estruturado) + renderização no bubble como chip.
- Endpoints de busca leves (`GET /mentions?q=&types=`) se necessário
  para fontes server-side.

Fora de escopo: menções em comentários de PR, menções cross-workspace.

## 3. Requisitos

- RF-001: `MentionService` (client) agrega providers com debounce 150ms,
  cache por query, cancelamento de requests antigos.
- RF-002: tipos `file`, `conversation`, `skill`, `automation`,
  `knowledge` com ícone + cor + label distintos; ranking por relevância
  (prefixo > substring; recentes primeiro em empate).
- RF-003: popup acessível (ARIA combobox/listbox), ↑↓/Enter/Esc,
  virtualização simples (>50 resultados), mobile-safe (max-height +
  scroll, position fixed quando viewport pequeno).
- RF-004: chip renderizado na mensagem enviada e no bubble do usuário;
  clique no chip navega/abre referência.
- RF-005: endpoint `GET /api/v1/mentions?q=&types=&limit=` (auth) para
  fontes server-side (conversations/skills/automations/knowledge);
  files continuam client-side (workspace FS via API existente).
- RF-006: testes — unit ranking/cache, bUnit composer (popup,
  teclado, seleção, chip), integração endpoint mentions, i18n ×8.

## 4. API Contract

- `GET /api/v1/mentions?q=...&types=file,conversation,...&limit=20`
  → `{ items: [{ type, id, label, snippet?, icon? }] }`.
- Formato de menção na mensagem: `@[label](type:id)` (parse tolerante).

## 5. Critérios de aceite

- **Dado** usuário digita `@auth` no composer, **quando** popup abre,
  **então** vê arquivos/conversas/skills/automations que casam, navegáveis
  por teclado.
- **Dado** menção selecionada, **quando** mensagem enviada, **então**
  bubble mostra chip clicável e o agente recebe referência estruturada.
- **Dado** viewport mobile, **quando** popup abre, **então** não corta
  fora da tela (scroll interno).

## 6. Task Plan

1. Endpoint `/mentions` + providers server-side.
2. `MentionService` client + providers file/conversation/skill/etc.
3. UI popup rico + teclado + mobile + chip render.
4. i18n ×8 + testes.

## 7. Deliverables

- Autocomplete rico end-to-end + testes

## 8. Referências

- OpenWebUiCode `27aeb87` (mobile composer popups), menções ricas
- Sandcastle `src/Sandcastle.Client/Components/ChatComposer.razor`

## 9. Guardrails

- Menções nunca quebram o envio da mensagem (parse tolerante; menção
  inválida vira texto puro).
- Busca server-side respeita permissões do usuário (só vê o que pode ver).
- Performance: debounce + cache + cancel; sem request por tecla.

## 10. DoD

- [ ] 5 tipos de menção funcionando; teclado/mobile ok; testes verdes;
  i18n ×8.
