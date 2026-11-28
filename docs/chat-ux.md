# Chat UX cloud parity

## English

SPEC-188 adds the conversation branching and contextual PR/CI control plane on top of the existing rich chat surface. `/api/v1/conversations/{id}/branches` lists the current conversation and related forks, `POST .../branch` creates a detached branch through the existing checkpoint/event fork pipeline, and `GET .../pr-status` exposes repository, PR number/state/link and placeholders for checks, review comments and preview links.

The conversation page now has a Branches tab that compares branch status/timestamps, opens sibling branches, starts a detached branch without blocking the main conversation, and shows linked PR/CI context. Rich activity streaming, markdown/source rendering, codeblock tooling, attachments, composer controls, checkpoints, commits, browser/desktop surfaces, usage/cost metadata and keyboard-accessible tabs remain implemented by the earlier chat SPECs; this slice connects those surfaces into the cloud-parity workflow.

## Português

A SPEC-188 adiciona o controle contextual de branches e PR/CI sobre a superfície rica de chat já existente. `/api/v1/conversations/{id}/branches` lista a conversa atual e forks relacionados, `POST .../branch` cria uma branch destacada pelo pipeline existente de fork por checkpoint/eventos e `GET .../pr-status` expõe repositório, número/estado/link do PR e placeholders para checks, review comments e previews.

A página da conversa agora tem a aba Branches para comparar status/timestamps, abrir branches irmãs, iniciar uma branch destacada sem bloquear a conversa principal e mostrar o contexto de PR/CI vinculado. Streaming rico de atividade, markdown/source, toolbar de codeblocks, attachments, controles do composer, checkpoints, commits, surfaces browser/desktop, metadados de uso/custo e tabs acessíveis por teclado continuam implementados pelas SPECs anteriores; este recorte conecta essas superfícies ao fluxo de paridade cloud.
