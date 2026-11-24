# SPEC — 178 · Team tools (mailbox inter-agentes) + delegate v2

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `team-agent-mailbox` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #528 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** agente orquestrador, **I want** enviar/receber mensagens entre
sub-agentes da mesma conversa e delegar tarefas com `wait`/persona
**so that** trabalho paralelo se coordena sem o orquestrador virar
gargalo — paridade com `team_task`/`team_message`/`team_status` e
`delegate_task(repo, persona, wait)` do OpenWebUiCode.

## 2. Contexto / escopo

Sandcastle tem `spawn_task` + catálogo declarativo `*.agent.md`
(SPEC-95) — mas filhos são fire-and-forget: não há caixa de mensagens
entre agentes, nem consulta de status por nome, nem delegate que espera
o resultado. O orquestrador só aprende o resultado no fim do filho.

Mudanças:

- `team_task` — cria tarefa nomeada no time da conversa (descrição,
  assignee opcional); lista/atualiza status (`todo`/`doing`/`done`).
- `team_message` — posta mensagem na mailbox do time (`to` opcional =
  agente; broadcast default); `team_status` — lê inbox + status das
  tasks do time.
- Mailbox por conversa: `TeamMailbox`/`TeamTask` records (migration
  dual); mensagens/tracks duram a vida da conversa; filhos do mesmo
  `parent_run_id` compartilham o time.
- `delegate_task` upgrade: parâmetros `repo` (worktree), `persona`
  (catálogo `*.agent.md`), `wait` (sync com timeout — resultado volta
  como tool result) e `parent_agent` para escopo da mailbox.
- Orquestrador recebe notificação de mailbox no próximo turn boundary
  (evento `team_message` no stream); sub-agente termina com resumo
  postado automaticamente na mailbox.

Fora de escopo: times cross-conversa, prioridade/locks de task,
replanning automático do orquestrador.

## 3. Requisitos

- RF-001: records `TeamTask`/`TeamMessage` + migration dual; escopo
  `conversation_id` + `team_id` (run raiz); TTL = lifetime da conversa.
- RF-002: tools `team_task` (create/list/update), `team_message`
  (send/inbox), `team_status` (tasks+inbox resumido) registradas só
  quando `Agent:TeamTools` (default on) e run tem `AllowSubAgents`.
- RF-003: `delegate_task` aceita `repo`/`persona`/`wait`/`timeout_sec`;
  `wait=true` bloqueia até done/timeout e devolve resumo — nunca expõe
  eventos internos do filho.
- RF-004: resumo automático do sub-agente postado na mailbox ao
  completar/falhar; eventos `team_task`/`team_message` persistidos e
  no stream.
- RF-005: guardrails — cap de inbox (`Agent:TeamMailboxMax`, default
  200, FIFO eviction), mensagens ≤8 KB, depth limit do spawn herdado.
- RF-006: testes — unit do mailbox service (CRUD, cap, scoping),
  integração delegate wait, tool registry gating, migration dual.

## 4. API Contract

- Tools internas (não REST): `team_task`, `team_message`,
  `team_status`, `delegate_task` (parâmetros novos).
- Eventos: `team_task_updated`, `team_message_posted`.
- Diagnóstico: `GET /app-conversations/{id}/runs/{runId}/team` → tasks +
  inbox (read-only, para UI/ops panel futuro).

## 5. Critérios de aceite

- **Dado** orquestrador com 2 filhos, **quando** filho A posta
  `team_message` para B, **então** B vê a mensagem no próximo turn.
- **Dado** `delegate_task(wait=true, timeout=60)`, **então** o tool
  result traz o resumo do filho ou timeout explícito.
- **Dado** `team_status`, **então** retorna tasks por status e últimas
  N mensagens sem vazar dados de outra conversa.

## 6. Task Plan

1. Records + migration dual + `TeamMailboxService`.
2. Tools team_* + gating + resumo auto do sub-agente.
3. `delegate_task` v2 (repo/persona/wait/timeout).
4. Endpoint diagnóstico + eventos.
5. Testes.

## 7. Deliverables

- Mailbox inter-agentes + delegate v2 com wait/persona/repo

## 8. Referências

- OpenWebUiCode `ChatTools/Tools/Team*` (`team_task`, `team_message`,
  `team_status`), `DelegateTaskTool`, `agents/*.agent.md`
- Sandcastle `Agent/Tools/` (`spawn_task`, catalog SPEC-95),
  `Agent/Subagents/`, `PermissionedToolRegistry`

## 9. Guardrails

- Mailbox é por conversa — nunca vaza entre conversas/tenants.
- `wait` tem timeout duro e respeita cancel da run pai.
- Resumo do filho é texto único — filho não injeta tool calls no pai.

## 10. DoD

- [ ] Mailbox + team tools funcionando entre filhos da mesma run.
- [ ] delegate wait/persona testados; migration dual verde.
