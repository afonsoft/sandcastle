# SPEC — 162 · Agendamentos avançados (cron, timezone, approval preset)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `automation-scheduler-advanced` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #492 · Issue #494 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário que agenda automações, **I want** expressar agendas em
cron com timezone própria e escolher o preset de aprovação da conversa
disparada **so that** eu tenho paridade com o scheduler avançado do
OpenWebUiCode (`d5184b3`) sem depender de UTC fixo.

## 2. Contexto / escopo

`AutomationScheduleKinds` = `interval|daily|weekly|once`, tudo em UTC
(`AutomationContracts.cs:4`); `AutomationSchedule.ComputeNextRun` é puro e
testado. `AutomationRecord` não tem timezone nem expressão cron.

Faltam:

- Kind `cron` com expressão validada (parser **Cronos**, só parsing — sem
  Quartz) e `TimeZoneId` IANA (default `UTC`) para `daily|weekly|cron`;
  `interval` ignora timezone.
- `ApprovalPreset` opcional (`allow|allow-readonly|plan`) aplicado às
  regras de permissão da conversa criada pelo kind `task`.
- UI: campos cron/timezone/approval preset em `/automations` + i18n ×8.
- Calendário (SPEC-160) passa a projetar `next_runs` respeitando o
  timezone da automação.

Fora de escopo: kind `run` separado — o kind `task` do sandcastle já cria
a conversa (equivalente ao `run` do OpenWebUiCode); o delta real é o
approval preset.

## 3. Requisitos

- RF-001: `AutomationScheduleKinds.Cron = "cron"`; expressão validada no
  create/update (400 em inválida); `ComputeNextRun` estendido para cron +
  timezone (IANA; inválido → fallback UTC com warning).
- RF-002: `AutomationRecord.TimeZoneId` (string, default `"UTC"`) +
  `AutomationRecord.CronExpression` (string?) — migration dual
  SQLite/Postgres; `ScheduleTimeUtc` permanece para daily/weekly.
- RF-003: `daily|weekly` passam a computar o próximo disparo no
  `TimeZoneId` da automação (convertendo para UTC no cálculo); contratos
  expõem `time_zone_id`, `cron_expression`, `approval_preset`.
- RF-004: `ApprovalPreset` (`allow|allow-readonly|plan`, default null =
  comportamento atual) gravado na automation e aplicado como regras de
  permissão da conversa criada (mapear para `AgentPermissionRule` do
  SPEC-29: `allow-readonly` → ask em tools Risky de escrita; `plan` →
  plan-mode overlay).
- RF-005: pacote NuGet **Cronos** (parser apenas); `packages.lock.json`
  regenerado.
- RF-006: UI — kind `cron` com campo de expressão + timezone (select dos
  `TimeZoneInfo.GetSystemTimeZones()` ou input IANA validado), approval
  preset; i18n ×8.
- RF-007: `GET /automations/schedule` (SPEC-160) projeta `next_runs`
  usando timezone/cron da automação.
- RF-008: testes — unit do `ComputeNextRun` (cron, DST, tz inválida,
  interval ignora tz), integração create/update 400, bUnit dos campos.

## 4. API Contract

- `AutomationUpsert`/`AutomationInfo` ganham `schedule_kind="cron"`,
  `cron_expression`, `time_zone_id`, `approval_preset`.
- 400 quando `cron` sem expressão válida ou `approval_preset` fora do
  conjunto.

## 5. Critérios de aceite

- **Dado** automation `cron="0 9 * * 1-5"` `time_zone_id="America/Sao_Paulo"`,
  **quando** consulto `next_runs`, **então** os horários batem 9h no fuso
  de São Paulo (convertidos para UTC corretamente, inclusive em DST).
- **Dado** `cron="não é cron"`, **quando** crio a automation, **então**
  recebo 400 com detalhe da expressão.
- **Dado** `approval_preset="plan"`, **quando** a automation dispara,
  **então** a conversa criada entra em plan mode.

## 6. Task Plan

1. Migration dual + records + options.
2. `Cronos` + `AutomationSchedule` (cron/tz) + validações.
3. Approval preset → permissões da conversa.
4. Endpoint/DTOs + golden OpenAPI.
5. UI campos + i18n ×8 + bUnit.
6. Testes unit/integração.

## 7. Deliverables

- Cron/timezone/approval preset + UI + testes + docs

## 8. Referências

- OpenWebUiCode `d5184b3` + SPEC
  `docs/specs/SPEC-20261115-scheduler-advanced.md`
- Cronos: https://github.com/HangfireIO/Cronos

## 9. Guardrails

- Parser apenas — nunca executar cron via Quartz/host.
- Timezone inválido nunca derruba o tick: fallback UTC + log.
- Approval preset nunca amplia permissões além do default do usuário.

## 10. DoD

- [ ] Cron + timezone + approval preset funcionais com testes.
- [ ] Migration dual; UI i18n ×8; docs atualizadas.
