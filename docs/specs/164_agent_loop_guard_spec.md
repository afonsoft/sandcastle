# SPEC — 164 · Guardas do loop do agente (same-tool, turn-end, stall)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-loop-guard` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Completed` |
| Ticket | #496 · Epic #492 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário rodando conversas longas, **I want** que o agente seja
interrompido quando entra em loop de tool calls repetidas, quando a run
termina de forma anômala ou quando trava sem progresso **so that** runs
não queimam tokens/tempo indefinidamente — paridade com o
`RepeatToolGuard` + turn-end guard + stall watch do OpenWebUiCode
(`8f416ef`, `8e05da7`, `6792153`).

## 2. Contexto / escopo

`AgentRunner.DispatchToolCallsAsync` executa as tool calls de cada turno
sem detector de repetição; o único bound é `MaxIterations`. Não há
detector de args idênticos, nem guarda de turn-end, nem watchdog de stall.

Faltam:

- **SameToolGuard**: N chamadas consecutivas da mesma tool (qualquer
  args) → veto com tool result de erro orientando o modelo a mudar de
  abordagem; contador reseta ao trocar de tool.
- **RepeatArgsGuard**: N chamadas idênticas (mesma tool + mesmo hash de
  args) → veto imediato (limite menor).
- **Turn-end guard**: run que termina sem mensagem assistant final ou com
  tool calls pendentes emite diagnóstico `turn_end_anomaly` e mensagem de
  fechamento sintética (nunca silêncio).
- **Stall watchdog**: sem progresso (nenhum evento durável novo) por
  `Agent:StallWatchdogSec` (default 300s) durante iteração → evento
  `agent_stall` + uma iteração de "nudge" (mensagem system sintética);
  stall persistente após `Agent:MaxStallNudges` (default 2) → abort com
  erro `stalled`.

Fora de escopo: alterar `MaxIterations`, eval_mode, subagents (guard vale
para o runner principal; subagents herdam via mesmo `AgentRunner`).

## 3. Requisitos

- RF-001: `AgentLoopGuard` (classe pura em `Sandcastle.Infrastructure/Agent`)
  com estado por run: `Register(toolName, argsHash)` →
  `LoopVerdict { Ok, VetoSameTool, VetoRepeatArgs }`; limites
  `Agent:MaxSameToolConsecutive` (default 6) e
  `Agent:MaxRepeatArgs` (default 3); reset ao trocar de tool; serializa
  args canonicamente (JSON sorted keys) para o hash.
- RF-002: veto injeta `ToolResult` de erro ("tool X repeated N times —
  change approach") + evento diagnóstico `loop_guard_veto`; a run
  continua (modelo pode se recuperar) até `MaxIterations`.
- RF-003: turn-end guard — no fim da run, se o último evento durável não
  for assistant/message e houver `turn.Calls` não resolvidas, emite
  `turn_end_anomaly` (severity warning) + mensagem assistant de fechamento
  "Run encerrada pelo guard de turn-end"; nunca deixa a conversa sem
  resposta visível.
- RF-004: stall watchdog — `RunActivityTracker` (já existente) expõe
  `LastProgressAt`; watchdog verifica a cada iteração; stall →
  `agent_stall` + nudge system; após `MaxStallNudges` → abort
  `stalled` com evento de erro.
- RF-005: config keys `Agent:MaxSameToolConsecutive`,
  `Agent:MaxRepeatArgs`, `Agent:StallWatchdogSec`,
  `Agent:MaxStallNudges` documentadas.
- RF-006: testes — unit do guard (limites, reset, hash canônico, veto),
  integração AgentRunner com LLM fake em loop (verifica veto + abort),
  turn-end anomaly, stall com relógio fake.

## 4. API Contract

- Sem endpoints novos. Novos códigos de diagnóstico/eventos:
  `loop_guard_veto`, `turn_end_anomaly`, `agent_stall`, erro `stalled`.
- `AgentLlmOptions`/config `Agent:*` estendido.

## 5. Critérios de aceite

- **Dado** modelo que chama `file_read` 7 vezes seguidas (args diferentes),
  **quando** o guard está ativo, **então** a 7ª é vetada com mensagem de
  erro e a run não entra em loop infinito.
- **Dado** modelo que repete a mesma call (tool+args) 3 vezes, **então**
  veto imediato na 3ª.
- **Dado** run sem progresso por 300s, **então** `agent_stall` é emitido
  e, persistindo, a run aborta com `stalled`.

## 6. Task Plan

1. `AgentLoopGuard` puro + testes unit.
2. Integração no `DispatchToolCallsAsync`/`ExecuteToolCallAsync` (veto).
3. Turn-end guard no fechamento da run.
4. Stall watchdog via `RunActivityTracker` + nudges.
5. Config + docs + testes de integração.

## 7. Deliverables

- Guardas + eventos + config + testes

## 8. Referências

- OpenWebUiCode `8f416ef` (RepeatToolGuard), `8e05da7` (turn-end guard),
  `6792153` (prompt-pack loop mechanisms)
- Sandcastle `src/Sandcastle.Infrastructure/Agent/AgentRunner.cs`

## 9. Guardrails

- Guard nunca bloqueia tools distintas legítimas (ex.: ler 10 arquivos
  diferentes em sequência é OK — só mesma tool consecutiva conta).
- Veto é recuperável: modelo recebe erro e pode mudar de estratégia.
- Watchdog não aborta runs saudáveis longas (progresso = qualquer evento
  durável, incluindo deltas coalesced).

## 10. DoD

- [ ] Guards implementados e testados; run em loop é interrompida.
- [ ] Eventos de diagnóstico emitidos; config documentada.
