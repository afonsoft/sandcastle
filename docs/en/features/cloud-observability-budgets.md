# Cloud observability, analytics and budgets

`Admin → analytics` shows token/cost time series, model aggregates, conversation rankings and a per-run trace waterfall. `Admin → budgets` shows effective limits, monthly usage, warning/exceeded states and enforcement.

Main endpoints: `GET /api/v1/admin/analytics/summary`, `/rankings`, `/export`, `GET /api/v1/admin/budgets`, `POST /api/v1/admin/budgets/reconcile` and `GET /api/v1/runs/{id}/trace`.

Analytics derive from the immutable event ledger (`TokenEvent`, `ActionEvent`, errors), with no persisted rollup and no migrations. `TokenEvent` now carries additive `latency_ms`. A periodic worker reconciles budgets, writes alert audit entries and applies optional event retention through `Observability:RetentionDays` (`0` disables cleanup).
