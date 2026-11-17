# Automations (SPEC-47)

Rotinas agendadas e gatilhos de webhook que disparam **conversas
start-task** com as credenciais do owner. A execução passa pelo pipeline
normal — quotas de plano, permissões e eventos V1 são idênticos a uma
conversa iniciada pela UI.

## Modelo

| Tabela | Conteúdo |
|---|---|
| `automations` | name, prompt (≤8KB), llm_model?, selected_repository?, schedule (kind/time/interval/weekday), enabled, next_run_at(+ticks), last_run_at, **misfire_policy**, **consecutive_failures** |
| `automation_runs` | started/finished/status(`running\|succeeded\|failed\|skipped`), conversation_id, error, **missed** |
| `automation_hooks` | token sha256 (o token em claro é exibido **uma vez**), last_used_at |

`schedule_kind`: `interval` (minutos ≥5), `daily`/`weekly` (`HH:mm` UTC,
weekday 0–6), `once` (desarma após disparar). `ComputeNextRun` é puro e
coberto por tabela-verdade.

### Robustez do scheduler (SPEC-161)

- **Paralelismo**: tick dispara devidas em paralelo limitado por
  `Automation:MaxConcurrent` (default 4); cada automation roda no próprio
  scope/DbContext — uma falha nunca bloqueia as demais.
- **Jitter determinístico ±5%** no `next_run_at` de schedules recorrentes
  (seed = id + ticks do disparo) para evitar thundering herd; `once`
  nunca recebe jitter.
- **Retry transitório**: falhas de `HttpRequestException`,
  `TaskCanceledException`, `TimeoutException`/`IOException` retentam até
  `Automation:MaxTransientAttempts` (default 3) com backoff exponencial +
  jitter. Falhas permanentes (validação, quota/plano) nunca retentam.
- **Circuit breaker**: `Automation:CircuitBreakerThreshold` falhas
  consecutivas (default 5) desabilitam a automation
  (`enabled=false`, `next_run_at=null`), gravam audit
  `automation_circuit_open` e notificam por webhook
  (`automation_disabled`). Sucesso zera o contador; `run-now` e
  re-enable manual também.
- **Misfire policy** (`misfire_policy: skip|run-once`, default
  `run-once`): se o servidor ficou fora além da graça
  `Automation:MisfireGraceSec` (default 300s), `skip` grava run
  `skipped` + `missed=true` sem disparar; `run-once` dispara uma única
  vez com `missed=true` e reagenda a partir de agora. Runs manuais e
  hooks nunca recebem jitter nem são afetados pela política.

Configuração (`Automation:*`):

| Key | Default | Efeito |
|---|---|---|
| `MaxConcurrent` | 4 | paralelismo máximo do dispatch |
| `MisfireGraceSec` | 300 | tolerância p/ atraso antes de misfire |
| `MaxTransientAttempts` | 3 | tentativas para falhas transitórias |
| `RetryBaseDelayMs` / `RetryMaxDelayMs` | 500 / 8000 | backoff exponencial + jitter |
| `CircuitBreakerThreshold` | 5 | falhas consecutivas antes de desabilitar |

## Scheduler

`AutomationScheduler` (BackgroundService) ticka a cada 30s sob lock
distribuído — uma réplica por vez dispara as automations devidas em
paralelo (cap `Automation:MaxConcurrent`, um scope/DbContext por
automation) e reconcilia runs `running` com o `ExecutionStatus` da
conversa linkada (`Finished→succeeded`, `Error/Archived→failed`),
aplicando os resultados ao circuit breaker. Não há fila persistente de
dispatch: quem atrasa é recuperado pela misfire policy na próxima
disponibilidade. Falha em uma automation não bloqueia as demais.

## API

| Method | Path | Notas |
|---|---|---|
| GET/POST | `/api/v1/automations` | list/create (validação: prompt ≤8KB, interval ≥5) |
| GET/PATCH/DELETE | `/api/v1/automations/{id}` | detail/update — `{enabled}` faz toggle |
| POST | `/api/v1/automations/{id}/run-now` | 202 `{run_id, conversation_id}`; 409 se já running |
| GET | `/api/v1/automations/{id}/runs?take=`, `/api/v1/automations/runs?from&to` | histórico |
| POST/DELETE | `/api/v1/automations/{id}/hooks`, `/api/v1/automations/hooks/{hookId}` | token 1× |
| POST | `/api/v1/hooks/{token}` | **anônimo** → 202; 404 em inválido (não vaza existência); rate-limit 10/min por token + 60/min por IP |

## Tool `routine`

Builtin (`Risky:true` → gate de confirmation-mode) que opera sobre as
automations do owner da conversa: `action=list|create|enable|disable|
delete`. Thin adapter do mesmo `AutomationService` — sem caminho
paralelo.

## Origem e audit

- A conversa criada recebe `agent_diagnostic` `code=automation` com
  `automation:{id}` — visível na timeline.
- Audit log: `automation_fired` + `automation_run_created` (actor,
  hook/ip quando aplicável).
- Quota negada → `AutomationRun(failed, error="quota: …")`.

## UI

`/automations` (sidebar): tabela com agenda humana, próxima execução,
toggle enabled, run-now e lista "próximas execuções" (top-10).
`/automations/{id}`: editor completo + histórico de runs (link p/
conversa) + criação de hook com token exibido uma única vez.

> Prompts de automation nunca devem conter secrets — são mensagens de
> usuário comuns armazenadas no banco.
