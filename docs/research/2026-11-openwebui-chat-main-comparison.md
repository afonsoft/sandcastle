# OpenWebUiCode main chat comparison — 2026-11-28

## English

Source reviewed: `afonsoft/OpenWebUiCode` `main` after the 2026-11-28 update, especially PRs #793–#807. Target: `afonsoft/sandcastle` after SPEC-189.

| Area | OpenWebUiCode main | Sandcastle status | Proposed action |
|---|---|---|---|
| GitHub PR tools | `gh_pulls`, `gh_view`, `gh_diff`, `gh_comment` expose open PRs, details, diffs and comments to the agent loop | Only `create_pull_request` exists; no agent-native read/review/comment loop | SPEC-191 |
| Rich document readers | `read_pdf`, `read_archive`, `read_sqlite` extract bounded content from PDFs, zip archives and SQLite databases | Generic `file_read` is text-oriented; no structured rich readers | SPEC-192 |
| Run cognition tools | `think`, `goal`, `wait`, `memory_reflect`; eager todo guidance in prompt | `think` and task tracker exist; no explicit current-goal tool, background wait or memory digest tool | SPEC-193 |
| Run phase UX | Single `analyzing` phase before first generating, matching Devin-style activity | Event phase display exists, but no canonical analyzing lifecycle contract | SPEC-193 |
| Advisor quality | Advisor advice carries `nit/concern/blocker` severity and renders severity in activity | Sandcastle has advisor prompt/settings but no normalized advisor severity event contract | SPEC-193 |
| Tool output UX | Tool summaries interpolate counts/paths; diff lines are preserved; mobile activity and insights polished | Sandcastle tool cards exist but lack the same summary interpolation/persistence guarantees | SPEC-193 |
| Tool identity persistence | Persisted tool names restored in activity history | Sandcastle renders raw event/tool data but does not have an explicit persisted-name contract | SPEC-193 |

### Recommended priority

1. SPEC-191 GitHub PR agent tools — highest functional leverage for cloud/Devin parity.
2. SPEC-192 rich document readers — improves knowledge/RAG ingestion and agent usefulness.
3. SPEC-193 run cognition/activity UX parity — bundles smaller runtime and UI contracts, avoiding many tiny SPECs.

No OpenWebUiCode code was copied; this is a conceptual parity analysis. All implementation must use Sandcastle architecture and tests.

## Português

Fonte analisada: `main` do `afonsoft/OpenWebUiCode` após a atualização de 28/11/2026, principalmente PRs #793–#807. Alvo: `afonsoft/sandcastle` após SPEC-189.

| Área | OpenWebUiCode main | Estado do Sandcastle | Ação proposta |
|---|---|---|---|
| Tools GitHub PR | `gh_pulls`, `gh_view`, `gh_diff`, `gh_comment` expõem PRs, detalhes, diffs e comentários ao agent loop | Só existe `create_pull_request`; não há loop agente-nativo de leitura/review/comentário | SPEC-191 |
| Leitores ricos | `read_pdf`, `read_archive`, `read_sqlite` extraem conteúdo limitado de PDFs, zips e SQLite | `file_read` é texto genérico; não há leitores estruturados | SPEC-192 |
| Cognição da run | `think`, `goal`, `wait`, `memory_reflect`; orientação eager todo no prompt | `think` e task tracker existem; faltam goal explícito, wait em background e digest de memória | SPEC-193 |
| UX da fase da run | Fase única `analyzing` antes do primeiro generating, no estilo Devin | Existe exibição de fase, mas não contrato canonical de analyzing | SPEC-193 |
| Qualidade do advisor | Conselho com severidade `nit/concern/blocker` no SSE/feed | Sandcastle tem prompt/settings de advisor, mas não contrato de severidade normalizado | SPEC-193 |
| UX de output de tools | Summaries interpolam counts/paths; linhas de diff preservadas; mobile/insights polidos | Tool cards existem, mas sem as mesmas garantias de interpolação/persistência | SPEC-193 |
| Persistência de tool names | Nomes persistidos restaurados no histórico de atividade | Sandcastle renderiza dados brutos de evento/tool, sem contrato explícito de nome persistido | SPEC-193 |

### Prioridade recomendada

1. SPEC-191 — maior alavancagem funcional para paridade cloud/Devin.
2. SPEC-192 — melhora knowledge/RAG e utilidade do agente.
3. SPEC-193 — agrupa contratos menores de runtime/UX sem criar várias SPECs pequenas.

Nenhum código do OpenWebUiCode foi copiado; isto é análise conceitual de paridade. A implementação deve usar arquitetura e testes do Sandcastle.
