# SPEC — 163 · Observabilidade de automações (stats, health, idempotência)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `automation-observability` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #495 · Epic #492 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário com automações webhook/agendadas, **I want** ver a saúde
de cada automação (taxa de sucesso, último erro, falhas consecutivas) e
garantir que retries de webhook não criem runs duplicadas **so that** eu
detecto problemas sem abrir cada run — paridade com o
`automation-observability` do OpenWebUiCode (`a0f1283`).

## 2. Contexto / escopo

`/automations` lista automations e runs; `GET /{id}/runs` existe;
`GET /schedule` (SPEC-160) projeta agenda. Falta:

- Endpoint de stats por automation (agregados + últimos runs).
- Badge de saúde na lista (verde/amarelo/vermelho) com tooltip.
- Idempotência de webhook: `Idempotency-Key` header ou SHA-256 do payload
  bruto; entrega aceita em 24h → resposta deduplicada sem criar run.

Fora de escopo: circuit breaker/auto-disable (SPEC-161 — este SPEC só
**expõe** `consecutive_failures` e `last_error` já calculados/armazenados
por ele; sem SPEC-161 os campos derivam dos runs).

## 3. Requisitos

- RF-001: `GET /api/v1/automations/{id}/stats` (auth + scope read) →
  `{total, succeeded, failed, skipped, success_rate_last_20,
  last_error, consecutive_failures, last_runs[10 desc]}`; 404 se a
  automation não existir/não for do usuário (mesma regra do `GET /{id}`).
- RF-002: `AutomationHealth.Level` (Application, puro e testável):
  `green` ≥90% ou sem falhas; `yellow` 70–90% ou 1–2 falhas consecutivas;
  `red` <70% ou ≥3 falhas consecutivas ou circuit-open (SPEC-161). Exposto
  no stats e na lista (`health` field).
- RF-003: UI — dot de saúde + tooltip (último erro) na lista de
  `/automations`; clique abre o stats (painel ou aba) com últimos runs;
  i18n ×8.
- RF-004: idempotência de webhook — migration dual com tabela
  `automation_hook_deliveries` (`hook_id`, `idempotency_key`,
  `payload_hash`, `received_at`, `run_id`, unique `(hook_id,
  idempotency_key)`); chave = header `Idempotency-Key` ou SHA-256 do body
  bruto; janela 24h; checagem **após** auth do token e **antes** do
  `FireAsync`; duplicata → `200 {deduplicated:true, run_id}` sem novo run.
- RF-005: testes — unit do health level e da chave de idempotência,
  integração stats/404/dedup (mesma chave, payload diferente, janela
  expirada), bUnit do badge.

## 4. API Contract

- `GET /api/v1/automations/{id}/stats` → estrutura do RF-001.
- `POST /api/v1/hooks/{token}` aceita `Idempotency-Key`; resposta
  deduplicada `200` com `deduplicated:true`.

## 5. Critérios de aceite

- **Dado** automation com 8 sucessos e 2 falhas nas últimas 10, **quando**
  abro stats, **então** `success_rate_last_20` reflete a janela e health =
  `green` (≥90%); com 3 falhas consecutivas → `red`.
- **Dado** mesmo `Idempotency-Key` em 2 POSTs de webhook em <24h, **quando**
  o segundo chega, **então** não cria run e responde `deduplicated:true`.

## 6. Task Plan

1. Migration dual + entity `AutomationHookDelivery` + índice.
2. `AutomationHealth` + stats no `AutomationService` + endpoint.
3. Dedup no `FireHookAsync` + DTOs.
4. UI badge + painel stats + i18n ×8 + bUnit.
5. Testes unit/integração.

## 7. Deliverables

- Stats + health badge + idempotência de webhook + testes

## 8. Referências

- OpenWebUiCode `a0f1283` + SPEC
  `docs/specs/SPEC-20261115-automation-observability.md`

## 9. Guardrails

- Stats nunca expõe prompt/segredos — só contadores, erro e ids.
- Dedup nunca bloqueia payload legítimo novo (chave diferente).
- Índice único composto obrigatório (hot path do webhook anônimo).

## 10. DoD

- [ ] Stats/health/idempotência implementados com testes.
- [ ] Migration dual; UI i18n ×8; golden OpenAPI regen.
