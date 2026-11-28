# Observabilidade cloud, analytics e budgets

`Admin → analytics` mostra série temporal de tokens/custo, agregados por modelo, ranking por conversa e waterfall de trace por run. `Admin → budgets` mostra limites efetivos, consumo mensal, warning/exceeded e enforcement.

Endpoints principais: `GET /api/v1/admin/analytics/summary`, `/rankings`, `/export`, `GET /api/v1/admin/budgets`, `POST /api/v1/admin/budgets/reconcile` e `GET /api/v1/runs/{id}/trace`.

A análise deriva do ledger imutável de eventos (`TokenEvent`, `ActionEvent`, erros), sem rollup persistido e sem migrations. `TokenEvent` agora carrega `latency_ms` aditivo. Um worker periódico reconcilia budgets, grava alertas em auditoria e aplica retenção opcional de eventos por `Observability:RetentionDays` (`0` desativa).
