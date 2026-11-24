# SPEC — 176 · Prompt cache tiers + context ops (rewind/snapshot/usage)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `prompt-cache-context-ops` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #526 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** operador pagando tokens, **I want** o system prompt dividido
em prefixo estático cacheável + sufixo dinâmico, e o agente capaz de
`rewind` a um checkpoint, inspecionar uso de contexto e recuperar turns
pré-compaction **so that** runs longas custam menos e o agente explora
sem medo — paridade com `PromptCacheTier`, `checkpoint/rewind`,
`context_snapshot`/`context_usage` e `history://` do OpenWebUiCode
(SPEC-20261103-agent-prompt-cache).

## 2. Contexto / escopo

`SystemPromptComposer` concatena todos os contributors num bloco só —
providers com prompt caching (Anthropic, OpenAI auto) não aproveitam
prefixo estável. `BuiltinTools.Checkpoint` marca ponto; falta o par
`rewind`. `BuiltinContextDb` cobre save/get/list/delete/summary; falta
snapshot versionado estrutural, `context_usage` e `history://` recovery
pós-compaction. Mentions `ref:`/`@` não propagam ao tool context.

Mudanças:

- `IAgentPromptContributor` ganha `CacheTier {Static,Dynamic}`
  (default Static); composer emite prefixo (Static, join estável) +
  sufixo (Dynamic: modo, data, reminders) com marcador
  `[[CACHE_BREAK]]`; provider Anthropic injeta
  `cache_control:{type:"ephemeral"}` no último bloco estático —
  config `Agent:PromptCache` (off|anthropic|auto).
- `rewind` tool: colapsa a conversa ao último `checkpoint` —
  events posteriores ficam `collapsed` (não deletados; `history://`
  URI recupera), resumo do trecho colapsado é injetado.
- `context_snapshot` (create/list/restore versionado por conversa —
  estende `BuiltinContextDb`), `context_usage` (tokens estimados por
  seção do prompt + janela restante via `HeuristicContextMeter`).
- `history://{turn|event_id}` URI tool-side resolve conteúdo
  pré-compaction/collapsed (read-only).
- `AttachedRefs`: menções `ref:alias`/`@path` do usuário propagadas ao
  tool context (parity OWC `PromptContext.AttachedRefs`).

Fora de escopo: cache multi-turn de conversa inteira (v2), métricas
de hit-rate de cache.

## 3. Requisitos

- RF-001: `CacheTier` + composer 2-blocos + marcador; ordering
  preservada dentro de cada tier (Order atual).
- RF-002: provider Anthropic aplica `cache_control` no boundary; demais
  providers recebem prompt concatenado idêntico ao atual (no-op).
- RF-003: `rewind` → events marcados `collapsed` + resumo injetado +
  `history://` resolve; `checkpoint` registra marker reutilizável.
- RF-004: `context_snapshot` CRUD versionado (record + migration dual)
  + `context_usage` reportando seções/tokens/janela.
- RF-005: `AttachedRefs` no `AgentPromptContext` + tool context;
  contrib de mentions parseia `ref:`/`@`.
- RF-006: testes — composer (ordem/tiers/marcador), rewind (collapse +
  recovery), snapshot restore, usage numbers, provider no-op vs
  anthropic.

## 4. API Contract

- Sem endpoints novos; tools novas `rewind`, `context_snapshot`,
  `context_usage`; schema `history://` documentado.
- Eventos: `context_collapsed`, `context_restored`, `context_snapshot`.

## 5. Critérios de aceite

- **Dado** provider Anthropic, **quando** contributors Static mudam 0
  e Dynamic muda, **então** só o sufixo varia entre turns (prefixo
  byte-idêntico → cache hit).
- **Dado** run com checkpoint + 10 events depois, **quando** `rewind`,
  **então** contexto volta ao checkpoint, trecho fica acessível via
  `history://` e a run segue.
- **Dado** `context_usage`, **então** retorna breakdown por seção com
  estimativa consistente com o meter.

## 6. Task Plan

1. Tier + composer 2-blocos + provider cache_control.
2. `rewind`/collapse + `history://`.
3. Snapshot versionado + usage + AttachedRefs.
4. Migration dual + eventos + docs.
5. Testes.

## 7. Deliverables

- Cache tiers + rewind + snapshot/usage + attached refs

## 8. Referências

- OpenWebUiCode `PromptCacheTier` (ISystemPromptContributor.cs),
  `CheckpointBuiltinTool`, `ContextSnapshotBuiltinTools`,
  `ContextUsageBuiltinTool`, `NewContextBuiltinTool`,
  `ConversationHistoryBuilder` (`history://`)
- Sandcastle `SystemPromptComposer.cs`, `PromptContributors.cs`,
  `BuiltinContextDb.cs`, `HeuristicContextMeter.cs`, SPEC-121/128

## 9. Guardrails

- Rewind nunca deleta eventos (collapse lógico; `history://` read-only).
- Cache tier não muda conteúdo — só posição; prompt não-Anthropic
  byte-idêntico ao atual.
- AttachedRefs só injetam refs explicitamente anexadas pelo usuário.

## 10. DoD

- [ ] Prefixo estável validado (byte-diff entre turns = só sufixo).
- [ ] Rewind + snapshot + usage testados; migration dual verde.
