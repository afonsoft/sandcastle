# SPEC — 161 · Robustez do scheduler de automações

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `automation-scheduler-robustness` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | Epic #TBD · Issue #TBD |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** operador com muitas automações agendadas, **I want** o tick do
scheduler disparar em paralelo limitado, tratar misses de forma
previsível, retentar falhas transitórias e desligar automaticamente
automações cronicamente falhas **so that** uma automação lenta ou quebrada
não bloqueia as demais nem spamma conversas — paridade com o
`scheduler robustness` do OpenWebUiCode (`7a9f4a4`).

## 2. Contexto / escopo

`AutomationScheduler` (tick 30s, lock distribuído único) chama
`IAutomationService.TickAsync`, que faz `foreach` sequencial nas devidas
(`AutomationService.cs:644`). Já existe overlap guard por automação
(run `running` → run `skipped`) e reconcile run→status da conversa.

Faltam, vs OpenWebUiCode:

- Dispatch paralelo limitado (`Automation:MaxConcurrent`, default 4)
  com `Parallel.ForEachAsync` — hoje um `FireAsync` lento atrasa todas.
- Misfire policy por automação (`skip|run-once`, default `run-once`) +
  flag `Missed` no run quando `NextRunAt` está além da graça
  (`Automation:MisfireGraceSec`, default 300).
- Retry de falhas transitórias no `FireAsync` (3 tentativas, backoff
  exponencial + jitter); erros permanentes (quota, validação) não retentam.
- Circuit breaker: `Automation:CircuitBreakerThreshold` (default 5)
  falhas consecutivas → `Enabled=false` + auditoria + notificação ao dono.
- Jitter de ±5% no `Reschedule` para evitar thundering herd.

Fora de escopo: cron/timezone/approval preset (SPEC-162), stats/health/
idempotência (SPEC-163), fila persistente de runs (o cap de
concorrência + próximo tick já dão backpressure; documentado no RF-006).

## 3. Requisitos

- RF-001: `TickAsync` dispara as devidas com concorrência limitada por
  `Automation:MaxConcurrent` (default 4, `Parallel.ForEachAsync`);
  overlap guard existente preservado (run `skipped` quando já há
  `running`).
- RF-002: `MisfirePolicy` (`skip|run-once`) + `Automation:MisfireGraceSec`
  (default 300s): `NextRunAt` mais velho que a graça → política `skip`
  registra run `skipped` com `Missed=true`; `run-once` dispara uma vez e
  reagenda do `now`. `AutomationRunRecord.Missed` + migration (dual
  SQLite/Postgres) + `AutomationRunInfo.missed`.
- RF-003: retry transitório no `FireAsync`: até
  `Automation:MaxTransientAttempts` (default 3) com backoff exponencial
  + jitter full; classifica transitório (`HttpRequestException`,
  `TaskCanceledException` por timeout, 5xx do gateway) vs permanente
  (`PlanLimitExceededException`, 4xx, validação) — permanente falha na
  hora. Cada tentativa gera run própria? **Não** — o run é criado uma
  vez; retry acontece dentro do `FireAsync` antes de marcar
  `succeeded/failed`.
- RF-004: circuit breaker — `AutomationRecord.ConsecutiveFailures`
  incrementa em run `failed`, zera em `succeeded`; ao atingir
  `Automation:CircuitBreakerThreshold` (default 5) → `Enabled=false`,
  audit `automation_circuit_open`, notificação ao dono via
  `IUserNotificationService` (tipo dedicado `automation_disabled`).
  `run-now` manual e re-enable resetam o contador.
- RF-005: jitter ±5% no `NextRunAt` calculado por `Reschedule`
  (determinístico com seed = automation id para testes).
- RF-006: sem fila persistente — saturação de concorrência deixa a
  automation devida para o próximo tick (log + métrica); documentado.
- RF-007: config keys documentadas em `docs/` (automations.md) e
  `.env.exemplo` se houver seção de automations.
- RF-008: testes — unit (classificação transitório/permanente, breaker,
  misfire, jitter determinístico), integração (cap de concorrência,
  auto-disable + notificação, missed flag).

## 4. API Contract

- Sem endpoints novos. `AutomationInfo` ganha `consecutive_failures`,
  `circuit_open` (bool derivado: `!Enabled && ConsecutiveFailures >=
  threshold`); `AutomationRunInfo` ganha `missed` (bool).
- Config: `Automation:MaxConcurrent`, `Automation:MisfireGraceSec`,
  `Automation:MaxTransientAttempts`, `Automation:CircuitBreakerThreshold`.

## 5. Critérios de aceite

- **Dado** 10 automations devidas e `MaxConcurrent=4`, **quando** o tick
  roda, **então** no máximo 4 `FireAsync` executam simultâneos e as
  demais ficam devidas para o próximo tick.
- **Dado** automation com 5 falhas consecutivas, **quando** a 5ª ocorre,
  **então** ela é desabilitada, audit registra `automation_circuit_open`
  e o dono recebe notificação.
- **Dado** `NextRunAt` 10min no passado com `MisfireGraceSec=300` e
  política `skip`, **quando** o tick roda, **então** um run `skipped` com
  `missed=true` é registrado sem disparar conversa.

## 6. Task Plan

1. Migration dual (`Missed`, `ConsecutiveFailures`) + records/config.
2. `Automation:Options` + retry classifier + breaker + jitter em
   `AutomationService`; paralelismo em `TickAsync`.
3. Notificação `automation_disabled` + audit.
4. DTOs (`missed`, `consecutive_failures`, `circuit_open`) + golden
   OpenAPI regen se aplicável.
5. Testes unit + integração; docs.

## 7. Deliverables

- Scheduler robusto + migration + DTOs + testes + docs

## 8. Referências

- OpenWebUiCode commit `7a9f4a4` (scheduler robustness) e SPEC
  `docs/specs/SPEC-20261115-scheduler-robustness.md`
- Sandcastle `src/Sandcastle.Infrastructure/Automations/`

## 9. Guardrails

- Manter lock distribuído por tick (multi-réplica); paralelismo é
  intra-réplica.
- Nunca retryar erro permanente (quota) — gasto duplo de LLM é pior que
  uma falha visível.
- Jitter determinístico em testes (seed por automation id).

## 10. DoD

- [ ] Paralelismo limitado, misfire, retry, breaker e jitter implementados.
- [ ] Migration dual aplicada; DTOs expostos; testes verdes.
- [ ] Docs de config atualizadas.
