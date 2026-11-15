# SPEC — 160 · Calendário de automações e execuções

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `automations-calendar` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #TBD · Epic #TBD |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário com automações agendadas, **I want** um calendário
mostrando execuções passadas (sucesso/falha) e futuras **so that** eu
entendo a agenda e detecto falhas sem abrir cada automação — paridade
com o `Calendar` do OpenWebUiCode (mês + camada de eventos + runs).

## 2. Contexto / escopo

`/automations` já existe (lista/CRUD/schedules). Falta a visão temporal:

- Rota `/automations/calendar` (ou tab `Calendar` dentro de
  `/automations` — decidir na implementação: **tab**, mantém um nav
  só) com grid mensal; cada dia lista próximas execuções previstas
  (derivadas do cron/rrule da automação) e execuções reais
  (`automation_runs`: status, duração, link).
- Navegar mês anterior/seguinte/hoje; click no dia abre painel lateral
  com runs do dia; click na run vai à conversa/log.
- Endpoint de apoio: `GET /api/v1/automations/schedule?from=&to=`
  retornando por automação os `next_runs[]` computados + `runs[]`
  reais no intervalo.

Fora de escopo: criar/editar automação pelo calendário, drag-reschedule,
timezones múltiplos (usa tz do usuário).

## 3. Requisitos

- RF-001: endpoint `GET /api/v1/automations/schedule?from&to` (max 62d)
  → `[{automation_id,name,next_runs[],runs[{id,status,started_at,
  duration_ms,conversation_id?}]}]`; cron parse server-side.
- RF-002: UI calendário mensal (grid CSS, sem lib nova): células com
  chips de run (cor por status) + marcador de next_runs; painel lateral
  do dia; i18n ×8 (nomes de mês/dia via `CultureInfo`).
- RF-003: click em run → link à conversa/audit; automação sem schedule
  não aparece.
- RF-004: testes — unit do cálculo de next_runs, integração do
  endpoint, bUnit do grid com dados fake.

## 4. API Contract

- `GET /api/v1/automations/schedule?from=2026-12-01&to=2026-12-31` →
  estrutura acima; 400 se intervalo >62d ou from>to.

## 5. Critérios de aceite

- **Dado** automação `FREQ=DAILY` com runs ontem (1 ok, 1 failed),
  **quando** abro o calendário, **então** o dia mostra chips verde/
  vermelho e próximos dias mostram o schedule previsto.
- **Dado** click na run failed, **então** navego ao detalhe/log.

## 6. Task Plan

1. Endpoint schedule + parser cron + testes.
2. `AutomationsCalendarTab` + grid + painel do dia + i18n ×8 + bUnit.
3. Link run→conversa + docs.

## 7. Deliverables

- Endpoint + tab calendário em `/automations` + i18n + testes

## 8. Referências

- OpenWebUiCode `Pages/Calendar.razor` · Sandcastle `/automations`

## 9. Guardrails

- Sem nova dependência de UI; grid próprio.
- Timezone: `TimeZoneInfo` do usuário (do perfil), default UTC.
- Não recomputar cron no client (server-side apenas).

## 10. DoD

- [ ] Calendário mostra next_runs e runs reais corretos por fuso.
- [ ] Link run→detalhe; intervalo >62d rejeitado; testes verdes.
