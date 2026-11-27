# .specs/ — SPECs SDD/ADD do Sandcastle

SPECs (Spec-Driven Development) do port do ecossistema **OpenHands** para
**.NET 10 / Blazor WASM**, seguindo o workflow afonsoft (`write-specs` →
`create-issues` → `execute-specs`) e o template SDD de `afonsoft/open-webui`.

O projeto opera em **AI-Driven Development (ADD)**: as SPECs SDD são o
artefato-fonte de verdade, escritas *para* agentes e executadas *por*
agentes — `execute-specs` é o caminho primário, com revisão humana nos
gates (aprovação de SPEC, merge).

## Convenção

- Os módulos 01–08 seguem a numeração definida em
  `sandcastle_full_specification_and_migration_guide.md` (raiz) (seção 8);
  09–13 cobrem lacunas identificadas para execução autônoma (Devin).
- Cada arquivo: `0. Metadata` → `7. Task Plan` + `8. Deliverables` +
  `9. Referências` + `10. Guardrails` + `11. DoD`.
- `Status`: `Draft` → `Approved` → `Done` → **mover para `docs/specs/`**
  ao entregar (arquivo morto, `Status: Completed`). Apenas SPECs ativas
  (`Draft`/`Approved`/`In implementation`) permanecem aqui.
- Numeração única `NN_name_spec.md`; em colisão, renumera-se para o próximo
  número livre e registra-se a origem (ver 68/69 abaixo).
- Documentação em pt-BR; código/endpoints em inglês.
- **Regra de licença**: upstream `enterprise/` e `OpenHands-Cloud/` são Polyform —
  as SPECs reproduzem conceitos/contratos públicos apenas (ver
  `migration-plan/research/risk-assessment.md`). Links para esses repos são
  sempre marcados "conceitos apenas".

## Índice — SPECs ativas

| SPEC | Domínio | Status | Issue |
|---|---|---|---|
| `70_sonar_vulnerabilities_spec.md` | SonarCloud — 52 vulnerabilities (16 regras) | `Completed` | PRs #159/#160 · #157 (Epic #152) |
| `71_sonar_bugs_spec.md` | SonarCloud — 32 bugs (7 regras) | `Completed` | PRs #159/#160 · #153 (Epic #152) |
| `72_sonar_critical_code_smells_spec.md` | SonarCloud — 121 smells BLOCKER/CRITICAL | `Completed` → docs/specs | #154 · PR #343 (Epic #152) |
| `73_sonar_major_code_smells_spec.md` | SonarCloud — 243 smells MAJOR | `Completed` (docs/specs/) | #155 · PR #345 |
| `74_sonar_minor_code_smells_spec.md` | SonarCloud — 331 smells MINOR/INFO | `Completed` (docs/specs/) | #156 · PRs #347/#348 |
| `89_sonar_deferred_refactors_spec.md` | Sonar — defer Lote D (S3776/S107/S3903/S1075) | `Completed` → docs/specs (superseded; filhos 101–107 entregues) | gap-analysis 2026-10-22 · Epic #152 |
| `92_client_drift_guards_spec.md` | Client/CI — drift guards (css-classes, form-a11y, i18n parity/placeholders) | `Completed` | PR #241 · #235 (Epic #231) |
| `93_perf_loadtest_harness_spec.md` | Tooling — mock LLM OpenAI-compat + loadtest (rps/conversations/subagents) | `Completed` | PR #242 · #236 (Epic #231) |
| `94_wasm_boot_resilience_spec.md` | Infra — boot.js autostart=false + `/framework-assets` mirror (proxies corporativos) | `Completed` | PR #243 · #237 (Epic #231) |
| `95_declarative_agent_catalog_spec.md` | Agent runtime — catálogo `*.agent.md` em camadas + `agent_catalog` tool (dep: SPEC-90) | `Completed` | PR #240 · #234 (Epic #231) |

## Arquivo morto — `docs/specs/`

SPECs entregues ficam em [`docs/specs/`](../docs/specs/README.md), com
`Status: Completed` e evidência de entrega (PR/commit) no índice —
incluindo 65/66/67 (2026-10-19), 75–78 (2026-10-20) e 83–87 (2026-10-22).
Guard: `scripts/check-spec-lifecycle.sh` falha se uma SPEC com status
terminal permanecer aqui (SPEC-82).

> **Renumbering (2026-10-19)**: `52_nextgen_improvements` → **68** e
> `53_enterprise_sandbox_hardening` → **69** para resolver colisões com
> `52_github_app_scm` e `53_scm_providers`. Referências de código foram
> atualizadas (`SPEC-53`→`SPEC-69` nos pontos enterprise).

## Fluxo

`write-specs` → aprovação (gate) → `create-issues` → `execute-specs`/`tdd-spec`
→ `qa-analyst` → `code-review-and-quality` → PR/merge → arquivar em
`docs/specs/`.

## Fontes de verdade

- `migration-plan/research/` — análise do upstream (evidência `file:line`).
- `docs/reference/openhands-upstream/` — extratos das docs oficiais
  (cloud-api, search-events OpenAPI, cli/local-setup).
- `docs/reference/upstream-baseline.md` — SHAs dos repos upstream
  (permalinks `blob/<sha>` usados nas SPECs).
- Baseline upstream: clones `OpenHands/*` (HEAD ~2026-10-12).

### Leva 2026-10-23 (gap-analysis)

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 96 | sonar-rescan-truth | Completed → docs/specs | #248 (verif. exclusões) |
| 97 | fuzz-findings | Completed → docs/specs | #218 (verif. nightly) |
| 98 | i18n-locale-slices | Completed → docs/specs | #250/#244 |
| 99 | spec89-deferred-split | Completed → docs/specs | gap-20261023 |
| 100 | epic-hygiene | Completed → docs/specs | gap-20261023 |

### Filhos SPEC-89 (defer Lote D)

| SPEC | Título | Status | Issue |
|---|---|---|---|
| 101 | s3776-cli-cognitive | Completed → docs/specs | PR #263 |
| 102 | s3776-infra-agent | Completed → docs/specs | PR #264 |
| 103 | s3776-endpoints | Completed → docs/specs | Epic #152 · PR #357–#362 |
| 104 | s107-many-params | Completed → docs/specs | Epic #152 · PR #352 |
| 105 | s3218-s127-shadow-loop | Completed → docs/specs | Epic #152 · PR pending |
| 106 | s1075-s3903-uris-ns | Completed → docs/specs | Epic #152 · PR #354 |
| 113 | desktop-surface | `Completed` → docs/specs | PR #284 · #283 |

### Leva 2026-10-24 (gap-analysis — Epic #265) — entregue

SPECs 107–110 entregues via PRs #271–#274 e arquivadas em `docs/specs/`.

### Leva 2026-11-04 (gap-analysis vs `~/repos/OpenWebUiCode`) — entregue

SPECs 116–124 entregues via PR #327 (Epic #317) e arquivadas em
`docs/specs/` — auditoria de paridade com o fork irmão: UI/design-system,
tool-call UX, tools do agente (21 → 45 builtins), hooks/permissões/
deferred-loading, exposição MCP/ACP e assets PWA.

### Leva 2026-11-05 (sandbox browser) — entregue

SPEC-131 entregue via PR #339 e arquivada em `docs/specs/` — Playwright +
`chrome-devtools-mcp` na imagem de runtime, env pass-through em
`docker exec` e servidor MCP builtin `chrome-devtools`.

### Leva 2026-11-08 (beta-readiness audit) — em revisão

Auditoria OWUIC-layout + OpenHands/Devin/OpenCode-core; matriz de gaps
publicada na conversa de origem. SPECs aprovadas 2026-11-08 → issues + implementação:

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 132 | beta-auth-ux | Delivered · PR #375 | #366 |
| 133 | web-search-tool | Delivered · PR #373 | #367 |
| 134 | beta-byok | Delivered · PR #377 | #368 |
| 135 | beta-conversation-ux | Delivered · PR #384 | #369 |
| 136 | beta-nav-visual-polish | Delivered · PR #374 | #370 |
| 137 | beta-release-housekeeping | Delivered · PR #372 | #371 |

### Leva 2026-11-10 (competitive gap analysis — PR loop) — Approved

Análise: `docs/research/2026-11-competitive-gap-analysis.md`. Onda A
(review → autofix → QA) fecha o loop do PR que OpenHands (Verification
Stack) e Devin (Review+Autofix) vendem como diferencial #1.
Aprovadas em 2026-11-10 pelo usuário — Epic acompanha no GitHub.

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 139 | brand-assets | Delivered · PR #439 | #436 |
| 140 | pr-review-agent | Delivered · PR #414 | #405 |
| 141 | ci-autofix-loop | Delivered · PR #416 | #406 |
| 142 | qa-agent-evidence | Delivered · PR #418 | #407 |
| 143 | code-scans | Delivered · PR #422 | #408 |
| 144 | batch-refactor | Delivered · PR #423 | #409 |
| 145 | repo-wiki-mcp | Delivered · PR #420 | #410 |
| 146 | multi-repo-conversations | Delivered · PR #421 | #411 |
| 147 | product-polish | Delivered · PR #413 | #412 |

### Próxima onda (Onda D — ecossistema) — Approved

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 148 | skills-marketplace | Delivered · PR #431 | #428 |
| 149 | model-router | Delivered · PR #432 | #429 |
| 150 | cli-handoff | Delivered · PR #433 | #430 |

### Próxima onda (Onda E — enterprise + eval) — Approved

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 151 | alert-integrations | Delivered · PR #448 | GAP-ALERT-INTEGRATIONS |
| 152 | benchmark-adapters | Delivered · PR #449 | #446 |
| 153 | hybrid-runner | Delivered · PR #463 | GAP-HYBRID-RUNNER · #447 |

### Próxima onda (Onda F — UX vs OpenWebUiCode) — Approved

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 154 | session-resume-fresh-sandbox | Delivered · PR #460 | #452 |
| 155 | conversation-ops-panel | Delivered · PR #462 | #453 |
| 156 | customize-hub | Delivered · PR #465 | #454 |
| 157 | workspace-config | Completed | #455 · PRs #473/#478 |
| 158 | account-settings | Completed | #456 · PR #483 |
| 159 | composer-voice-stt | Completed | #457 · PR #485 |
| 160 | automations-calendar | Completed | #458 · PR #487 |

### Onda G — paridade OpenWebUiCode (nov/2026) — Approved

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 161 | automation-scheduler-robustness | Completed | #493 · PR #503 |
| 162 | automation-scheduler-advanced | Completed | #494 · PR #505 |
| 163 | automation-observability | Completed | #495 · PR #534 |
| 164 | agent-loop-guard | Completed | #496 · Epic #492 |
| 165 | hybrid-code-search | Completed | #497 · Epic #492 |
| 166 | session-insights-v2 | Completed | #498 · PR #540 |
| 167 | model-routing-v2 | Completed | #499 · PR #542 |
| 168 | skills-allowed-tools | Completed | #500 · PR #544 |
| 169 | plan-mode-v2 | Completed | #501 · PR #546 |
| 170 | composer-knowledge-autocomplete | Completed | #502 · Epic #492 |

### Onda H — agent runtime vs OpenWebUiCode (dez/2026) — Approved

Gap-analysis Sandcastle × `~/repos/OpenWebUiCode` (agente no container,
prompt/tools, fusion, mobile, healthcheck, archive/delete, blueprint).
Diretriz: `.agents/` é o diretório canônico do harness — `.devin`/
`.claude` lidos apenas como compat (nunca escritos). Blueprint do
próprio repo em `.agents/blueprint.yaml`. Aprovadas 2026-11-24 — Epic
#520.

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 171 | conversation-archive-ux (archive/unarchive + delete v2) | Approved | #521 · Epic #520 |
| 172 | blueprint-v2 (fases initialize/maintenance, knowledge, secrets, test) | Approved | #522 · Epic #520 |
| 173 | repo-hooks-discovery (`.agents/hooks.json` + compat `.devin`/`.claude`) | Approved | #523 · Epic #520 |
| 174 | tool-safety-tiers (tiers, command-risk, arg repair, paralelismo) | Approved | #524 · Epic #520 |
| 175 | sandbox-agent-health (`/alive` degraded, watchdog, exec cancel/batch) | Approved | #525 · Epic #520 |
| 176 | prompt-cache-context-ops (cache tiers, rewind, snapshot/usage) | Approved | #526 · Epic #520 |
| 177 | mobile-workspace-ux (tabs, touch, safe-area) | Approved | #527 · Epic #520 |
| 178 | team-agent-mailbox (`team_*` tools + delegate v2) | Approved | #528 · Epic #520 |

### Onda I — cloud product parity deep dive (dez/2026) — Draft

Deep dive Sandcastle × OpenWebUiCode × OpenHands × OpenHands-Cloud ×
Devin Cloud, registrado em
`docs/research/2026-11-openwebui-openhands-cloud-deep-dive.md`. SPECs
criadas como **Draft** em 2026-11-27; ainda sem issues/epic e sem
implementação até aprovação explícita.

| SPEC | Título | Status | Ticket |
|---|---|---|---|
| 179 | devin-navigation-shell | Draft | TBD |
| 180 | settings-profiles-control-center | Draft | TBD |
| 181 | agent-definitions-prompt-runtime | Draft | TBD |
| 182 | agent-tool-parity-v2 | Draft | TBD |
| 183 | workflow-factories-autonomous-delivery | Draft | TBD |
| 184 | cloud-runtime-control-plane | Draft | TBD |
| 185 | cloud-observability-budgets | Draft | TBD |
| 186 | collaboration-workspace | Draft | TBD |
| 187 | extensions-integrations-hub | Draft | TBD |
| 188 | chat-ux-cloud-parity | Draft | TBD |
| 189 | knowledge-rag-context-platform | Draft | TBD |
| 190 | enterprise-cloud-lifecycle | Draft | TBD |
