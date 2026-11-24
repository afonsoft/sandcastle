# SPEC — 171 · Arquivar/desarquivar conversas + delete v2

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `conversation-archive-ux` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #521 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário com muitas conversas, **I want** arquivar conversas sem
perdê-las, desarquivar sob demanda e excluir com escopo claro **so that**
a lista fica limpa sem destruir histórico — paridade com o ciclo
archive/meta do OpenWebUiCode (`ChatArchiveMeta`, `Archived.razor`,
`POST /{id}/archive`, `archive/all`).

## 2. Contexto / escopo

Hoje `AppConversationService.DeleteAsync` apenas marca
`ExecutionStatus.Archived` e derruba o sandbox — "delete" e "archive"
são a mesma operação, sem restore, sem lista de arquivadas, sem resumo e
sem purge. O ícone `archive` existe em `Icons.cs` órfão.

Faltam:

- `POST /{id}/archive` (toggle) + `POST /{id}/unarchive` + `GET
  /app-conversations/archived` (+ `/archived/count`).
- Unarchive re-provisiona sandbox sob demanda (resume lazy — reusa o
  fluxo SPEC-154 session-resume-fresh-sandbox).
- `ConversationArchiveMeta` (record + migration dual): `status`
  (`archived`/`deleted`), `summary` — resumo LLM no archive com fallback
  determinístico, reusado no delete.
- Sidebar/seção "Archived" no Client + ações nos cards (archive,
  unarchive, delete) + filtro default excluindo arquivadas (já existe
  no `SearchAsync`).
- Delete v2: cascade explícito (sandbox, workspace/worktree,
  checkpoints, secrets efêmeros, eventos) + purge agendado (hard delete
  de `Archived` > `Retention:PurgeArchivedAfterDays`, default 30; 0 =
  nunca).

Fora de escopo: delete-all admin, export no delete (TrajectoryExporter
já cobre sob demanda).

## 3. Requisitos

- RF-001: endpoints archive/unarchive/list/count + erro 404/409
  coerentes; archive é idempotente e suspende/derruba sandbox
  preservando workspace; unarchive marca `Ready` e re-provisiona no
  próximo send/resume.
- RF-002: `ConversationArchiveMetaRecord` + migration dual; archive e
  delete gravam status + summary (LLM via role `tasks`, fallback
  determinístico "N turns, M events").
- RF-003: Client — item "Archived" na `MainLayout`/lista, página de
  arquivadas com unarchive/delete, ações de contexto nos cards; i18n
  nas 8 locales (parity check `check-i18n-parity.py`).
- RF-004: delete documenta cascade + roda cleanup; purge worker
  (BackgroundService) hard-deleta registros expirados + audit event.
- RF-005: testes — unit do service (toggle, meta, cascade), integração
  dos endpoints, migration dual, bUnit da seção Archived.

## 4. API Contract

- `POST /api/v1/app-conversations/{id}/archive` → `{archived: bool}`
- `POST /api/v1/app-conversations/{id}/unarchive`
- `GET /api/v1/app-conversations/archived?limit=&page_id=` → page
- `GET /api/v1/app-conversations/archived/count`
- Divergência upstream: upstream não tem archive — extensão documentada.

## 5. Critérios de aceite

- **Dado** conversa ativa, **quando** archive, **então** sai da lista
  default, aparece em `/archived` com resumo, sandbox é liberado.
- **Dado** conversa arquivada, **quando** unarchive + send-message,
  **então** sandbox sobe sob demanda e a conversa volta operacional.
- **Dado** `Archived` mais velho que o retention, **quando** o purge
  roda, **então** registros e artefatos são removidos com audit.

## 6. Task Plan

1. Record + migration dual + service (toggle/meta/cascade).
2. Endpoints + DTOs + OpenAPI golden.
3. Client (seção Archived + ações) + i18n.
4. Purge worker + config.
5. Testes unit/integration/bUnit + drift guards.

## 7. Deliverables

- Endpoints + UI de archive/unarchive + meta com resumo + purge

## 8. Referências

- OpenWebUiCode `ChatEndpoints.cs` (`ToggleArchiveAsync`,
  `ListArchivedAsync`, `RecordDeletedMetaAsync`), `Archived.razor`,
  migration `ChatArchiveMeta`
- Sandcastle `AppConversationService.DeleteAsync`, `ConversationUxService`,
  `SandboxSnapshotEndpoints`, SPEC-154 (resume fresh sandbox)

## 9. Guardrails

- Archive nunca perde eventos/workspace; purge é opt-out
  (`Retention:PurgeArchivedAfterDays=0` desliga) e emite audit.
- Unarchive não recria sandbox até ser necessário (lazy).
- Resumo LLM nunca bloqueia o archive (fallback determinístico).

## 10. DoD

- [ ] Archive/unarchive + lista + resumo funcionando E2E.
- [ ] Purge + cascade testados; drift guards verdes.
