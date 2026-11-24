# SPEC — 175 · Health do SandboxAgent + watchdog de sandbox

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `sandbox-agent-health` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #525 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** operador do Sandcastle, **I want** o host sondando `/alive`
dos sandboxes ativos, o agent reportando degradação real (disco, RSS,
workspace) e runs travadas substituídas automaticamente **so that**
sandbox morto é detectado antes do timeout do request e a conversa se
recupera — estendendo `/api/v1/readiness` e o fluxo SPEC-154
(resume em sandbox fresco).

## 2. Contexto / escopo

Hoje: `/api/v1/readiness` cobre db/docker/llm/cache/egress; o
`SandboxAgent` expõe `/alive` (uptime/sessions) e o host só descobre
morte via timeout de request. SPEC-164 cobre stall *dentro* do runner;
esta SPEC cobre liveness do sandbox + substituição. Não há `/health`
simples para load balancer nem sonda de surfaces.

Mudanças:

- `GET /alive` ganha `checks` (`disk_free_mb`, `workspace_ok`,
  `rss_mb`, `sessions`) + `status: ok|degraded` — degraded quando
  disco < `SANDBOX_MIN_DISK_MB` (default 256) ou workdir ausente.
- `GET /health` lightweight (processo de pé, sem checks) no
  SandboxAgent e no RuntimeApi (`/healthz` já existe — alias).
- Host: `SandboxHealthProbeService` (BackgroundService) — a cada
  `Sandbox:HealthProbeSec` (default 60) chama `/alive` dos sandboxes
  `running`; falhas consecutivas ≥ `Sandbox:UnhealthyAfter` (default 3)
  → status `unhealthy` + evento `sandbox_unhealthy`; recuperação →
  `sandbox_recovered`.
- Watchdog de run: run `running` cujo sandbox está `unhealthy` →
  pausa + re-provision automática em sandbox fresco (reusa SPEC-154) ou
  erro `sandbox_lost` quando não resumível.
- `/api/v1/readiness` agrega `sandboxes: {running, unhealthy}`; chip de
  saúde do sandbox na UI da conversa.
- `POST /exec` com `correlation_id` + `DELETE /exec/{id}` (cancel de
  processo no container) + `POST /files/batch` (multi-path write) —
  higiene do protocolo agent.

Fora de escopo: métricas Prometheus do agent (OTel segue SPEC-13),
auto-healing via snapshot quente (commit `7efce5d` — avaliar como
follow-up).

## 3. Requisitos

- RF-001: `/alive` com `checks` + `degraded`; `/health` nos dois
  serviços de container.
- RF-002: probe host-side com thresholds/config; transições de status
  `running→unhealthy→(running|lost)` com eventos duráveis.
- RF-003: substituição de run — unhealthy persistente → resume em
  sandbox novo (SPEC-154) ou `sandbox_lost`; nunca 2 sandboxes ativos
  para a mesma conversa.
- RF-004: readiness agregado + `GET /app-conversations/{id}/runtime`
  devolve `sandbox_health`; client mostra chip/estado.
- RF-005: `correlation_id` + cancel + `/files/batch` no SandboxAgent;
  schemas versionados no contrato agent.
- RF-006: testes — unit do probe (transições, backoff), unit do
  `/alive` degraded, integração unhealthy→replace, cancel/batch.

## 4. API Contract

- SandboxAgent: `GET /alive` (estendido), `GET /health`,
  `DELETE /exec/{id}`, `POST /files/batch`.
- Api: readiness agregado; `sandbox_health` no runtime DTO.
- Eventos: `sandbox_unhealthy`, `sandbox_recovered`, `sandbox_lost`.

## 5. Critérios de aceite

- **Dado** sandbox parado de responder, **quando** 3 probes falham,
  **então** `sandbox_unhealthy` é emitido e a run é re-provisionada ou
  marcada `sandbox_lost`.
- **Dado** disco < limiar, **então** `/alive` retorna `degraded` com
  o check falho.
- **Dado** exec longo, **quando** `DELETE /exec/{id}`, **então** o
  processo morre no container e a run observa cancel.

## 6. Task Plan

1. `/alive` checks + `/health` + correlation/cancel/batch no agent.
2. Probe host + transições + eventos.
3. Substituição de run (SPEC-154 reuse) + readiness/DTO.
4. Client chip.
5. Testes.

## 7. Deliverables

- Liveness real do agent + substituição de sandbox + protocolo v2
  (cancel/batch/correlation)

## 8. Referências

- Sandcastle `SandboxAgent/AgentHandlers.cs`, `HealthEndpoints.cs`,
  `SandboxService.cs`, `WarmPoolService.cs`, SPEC-154
- OpenWebUiCode heartbeat watchdog + run ownership (`ChatRunExecutor`);
  commit `7efce5d` (auto-healing — descartado, reaproveitar conceito)

## 9. Guardrails

- Probe nunca derruba sandbox saudável (falso-negativo só após N
  falhas consecutivas + jitter).
- Re-provision não perde workspace (bind/snapshot); conversa nunca
  fica com 2 sandboxes.
- `degraded` informa, não bloqueia exec.

## 10. DoD

- [ ] Unhealthy→replace validado em integração; `/alive` degraded.
- [ ] Cancel/batch funcionando; readiness agregado + chip.
