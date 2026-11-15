# SPEC — 155 · Painel operacional da run (Jobs / MCP / Info + layout adaptável)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `conversation-ops-panel` |
| Type | `Frontend` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #TBD · Epic #TBD |
| Scope | `src/Sandcastle.Client/` + `tests/` |

## 1. User Story

**As a** usuário acompanhando uma run, **I want** ver num painel lateral
o que o agente está fazendo — tarefas do plano, arquivos alterados, jobs
em execução, servidores MCP conectados e metadados da run — **so that**
eu entendo o estado sem vasculhar o stream de eventos. Paridade com o
`ChatWorkspacePanel` do OpenWebUiCode (`Tasks | Changes | Jobs | MCPs |
Info`, drawer mobile, divisor redimensionável).

## 2. Contexto / escopo

A `ConversationPage` já tem split chat|workbench com tabs
IDE/terminal/browser/desktop/files/diff/commits/usage
(`ConversationPage.razor:113-180`). Faltam:

- **Tab `Jobs`**: lista de jobs/processos do agente na sandbox
  (`GET /sandboxes/{id}/jobs` — o sandbox-agent já expõe jobs p/ SPEC-131;
  conferir e expor via API se faltar) com status, comando, duração e
  ação "ver logs" (abre `TerminalTab` filtrado ou dialog read-only).
- **Tab `MCPs`**: servidores MCP da conversa, estado de conexão
  (connected/failed/connecting), tools count por servidor; reusa dados
  de `GET /app-conversations/{id}/mcp-status` (expor se não existir —
  o boot já reporta MCPs no ledger).
- **Tab `Info`**: metadados da run — model, agent_type, repo/branch,
  token totals, custo acumulado, tempo de run, sandbox id/image,
  `rehydrated` flag (SPEC-154).
- **Layout adaptável**: divisor arrastável entre pane-left e pane-right
  (largura persistida em `localStorage` por usuário), breakpoint mobile
  → pane-right vira drawer/bottom-sheet sobre o chat.

Fora de escopo: edição de MCPs pela tab (config fica em Settings/MCP),
kill de job pelo painel, métricas históricas.

## 3. Requisitos

- RF-001: novas tabs `Jobs`, `MCPs`, `Info` na tab-bar da
  `ConversationPage` (i18n ×8), dados via polling leve (5s) enquanto a
  run estiver ativa; congelam no estado final ao terminar.
- RF-002: `GET /api/v1/app-conversations/{id}/runtime` — payload único
  `{jobs[], mcp_servers[], info{...}}` para alimentar as 3 tabs numa
  chamada (evita N polls); auth/ownership padrão.
- RF-003: divisor arrastável (pointer events, min 280px/max 70%,
  `localStorage` key `conv-split-pct`); a11y — separador com
  `role="separator"` + setas do teclado.
- RF-004: mobile (<768px) — pane-right vira bottom-sheet com handle;
  abre pela tab-bar colapsada.
- RF-005: tab `Info` mostra `sandbox_rehydrated`/warning da SPEC-154
  quando presente.

## 4. API Contract

- `GET /api/v1/app-conversations/{id}/runtime` →
  ```json
  {"jobs":[{"id","command","status","started_at","duration_ms"}],
   "mcp_servers":[{"name","status","tools_count","error?"}],
   "info":{"model","agent_type","repository","branch",
           "tokens_in","tokens_out","cost_usd","run_ms",
           "sandbox_id","sandbox_image","rehydrated"}}
  ```
- 404 conversa inexistente; 403 sem ownership; 200 com arrays vazios
  quando ainda sem sandbox.

## 5. Critérios de aceite

- **Dado** run ativa com MCP conectado, **quando** abro a tab MCPs,
  **então** vejo o servidor, status e nº de tools.
- **Dado** job rodando na sandbox, **quando** abro Jobs, **então**
  vejo comando/status/duração atualizando.
- **Dado** drag do divisor pra 60%, **quando** recarrego a página,
  **então** a largura persiste.
- **Dado** viewport 375px, **quando** abro a conversa, **então** o
  painel abre como bottom-sheet sem quebrar o chat.

## 6. Task Plan

1. Endpoint `/runtime` agregando jobs + MCP status + info + testes.
2. `JobsTab`, `McpsTab`, `InfoTab` components + polling + testes bUnit.
3. Divisor arrastável + persistência + a11y + teste.
4. Bottom-sheet mobile + teste visual.
5. i18n ×8 + drift guards.

## 7. Deliverables

- `ConversationRuntimeEndpoints` + 3 components + divider/sheet
- i18n ×8 + testes bUnit + golden OpenAPI

## 8. Referências

- OpenWebUiCode `ChatWorkspacePanel.razor` (tabs + drawer + resize)
- `ConversationPage.razor` split atual · SPEC-131 (jobs/MCP sandbox)

## 9. Guardrails

- Polling só com run ativa ou tab visível (sem wake-loop em background).
- Sem mutação: painel é read-only nesta SPEC.
- i18n obrigatório ×8; drift guards verdes.

## 10. DoD

- [ ] 3 tabs vivas durante e após a run.
- [ ] Divisor persiste e é acessível por teclado.
- [ ] Mobile bottom-sheet sem regressão do split desktop.
- [ ] format/build/test verdes + golden OpenAPI regen.
