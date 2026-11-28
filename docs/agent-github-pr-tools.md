# Agent GitHub pull-request tools

## English

SPEC-191 adds four agent-loop tools so a run can inspect and discuss the pull-request lifecycle of the repository selected by its conversation without shelling out to `gh`:

| Tool | Risk | Behavior |
| --- | --- | --- |
| `github_list_pull_requests` | read-only | Lists bounded PR summaries filtered by `open`, `closed`, or `all`. |
| `github_view_pull_request` | read-only | Returns metadata, a redacted body excerpt, check-run counts, review counts, and changed-file totals. |
| `github_read_pull_request_diff` | read-only | Returns a bounded file/patch projection with path filtering, patch truncation, and binary-file markers. |
| `github_comment_pull_request` | risky/write | Posts an issue comment on a PR through the same confirmation path as other mutating tools. |

Repository arguments are scoped to the repositories selected by the conversation. The primary repository is the default; a full name (`owner/repo`) or the multi-repo short name can select another configured repository. Outputs are deterministic, bounded, redacted, and citation-friendly.

Comments are limited to 4,000 characters, redacted before submission, and audited as `scm.pr_comment` with the pull request number, repository, provider comment id, tool name, and client idempotency key. When no key is supplied, Sandcastle derives one from the conversation, repository, PR number, and body.

## Português

A SPEC-191 adiciona quatro tools ao loop do agente para inspecionar e discutir o ciclo de vida de pull requests do repositório selecionado pela conversa sem depender de shell/`gh`:

| Tool | Risco | Comportamento |
| --- | --- | --- |
| `github_list_pull_requests` | somente leitura | Lista resumos limitados de PRs filtrados por `open`, `closed` ou `all`. |
| `github_view_pull_request` | somente leitura | Retorna metadados, excerpt redigido do body, contagens de checks, contagens de reviews e totais de arquivos alterados. |
| `github_read_pull_request_diff` | somente leitura | Retorna projeção limitada de arquivos/patches com filtro por path, truncamento e marcador de binário. |
| `github_comment_pull_request` | arriscada/escrita | Publica comentário de issue no PR pelo mesmo caminho de confirmação das demais tools mutáveis. |

O argumento de repositório é restrito aos repositórios selecionados pela conversa. O primário é o default; nome completo (`owner/repo`) ou short-name multi-repo podem selecionar outro repositório configurado. Saídas são determinísticas, limitadas, redigidas e adequadas para citação.

Comentários têm limite de 4.000 caracteres, são redigidos antes do envio e auditados como `scm.pr_comment` com número do PR, repositório, id do comentário no provider, nome da tool e chave de idempotência do cliente. Sem chave explícita, o Sandcastle deriva uma da conversa, repositório, número do PR e corpo.
