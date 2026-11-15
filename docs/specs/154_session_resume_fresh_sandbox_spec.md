# SPEC — 154 · Session resume em sandbox novo (reidratação de checkpoint)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `session-resume-fresh-sandbox` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM · Docker` |
| Repository | `afonsoft/sandcastle` |
| Status | `Completed` |
| Ticket | #452 · Epic #451 |
| Scope | `src/` + `tests/` + `docker-compose.beta.yaml` |

## 1. User Story

**As a** usuário, **I want** retomar uma conversa cuja sandbox original
foi destruída (restart do host, GC, crash) **so that** a execução volta
sem intervenção manual — a lacuna nº 1 do roadmap pós-beta
(`docs/en/comparison.md` §roadmap: "resume on NEW sandbox (rehydrate
checkpoint)") e paridade com Devin/OpenWebUI, que reatam runs após
reload/restart.

## 2. Contexto / escopo

Hoje `Resume` assume a sandbox viva. Quando o container morre:

- o resume falha ou a conversa fica órfã;
- o workdir some (checkpoints `.ohn-checkpoints` vivem **dentro** do
  container — decisão registrada na SPEC-135).

Implementação proposta:

- **Snapshot persistente de workdir**: no stop/checkpoint do turno,
  exportar o estado do workdir (tar stream via `docker cp`-equivalente
  do `SandboxService`) para volume/dir host-side indexado por
  `conversation_id` + `checkpoint_id` (ex.: `Sandbox__StateDir/
  workdirs/{conversationId}/{checkpointId}.tar.zst`).
- **Resume com reidratação**: `POST /app-conversations/{id}/resume`
  detecta sandbox ausente/morta → provisiona nova sandbox → aplica o
  tar do último checkpoint válido → restaura env/secrets da conversa →
  segue o fluxo atual de resume (republicar eventos pendentes, run
  continua). Emitir evento `sandbox_rehydrated {from_checkpoint_id}`
  no ledger.
- **GC do state dir**: limpeza por `conversation` delete + TTL
  (`Sandbox__StateRetentionDays`, default 30) no worker de manutenção.

Fora de escopo: migração de volumes Docker entre hosts, resume
cross-region, restore parcial por path, snapshots incrementais.

## 3. Requisitos

- RF-001: `CheckpointService` ganha `ExportWorkdirAsync(conversationId,
  checkpointId)` que materializa o tar do workdir no state dir; chamado
  no auto-checkpoint por turno (best-effort, falha não bloqueia o turno).
- RF-002: `ResumeConversation` — quando `sandbox == null || !alive`,
  segue caminho de reidratação: nova sandbox (`SandboxService.Create`),
  `ImportWorkdirAsync` aplica o tar mais recente, segue fluxo normal;
  evento `sandbox_rehydrated` no ledger + log estruturado.
- RF-003: sem checkpoint exportado → resume em sandbox nova com workdir
  vazio + aviso `conversation_state {rehydrated=false,
  warning="no_snapshot"}` — nunca erro silencioso.
- RF-004: cleanup — `DELETE /app-conversations/{id}` remove o state dir
  da conversa; job de manutenção varre dirs órfãos e > TTL.
- RF-005: `POST /checkpoints/{id}/revert` em sandbox reidratada funciona
  para checkpoints exportados (não só os que sobreviveram no container).
- RF-006: config `Sandbox__StateDir` (default `<DataDir>/workdir-snapshots`)
  e `Sandbox__StateRetentionDays` documentadas em `.env.exemplo` +
  `docs/installation/environment-variables.md` (PT+EN).

## 4. API Contract

Sem rotas novas. Semântica alterada:

- `POST /api/v1/app-conversations/{id}/resume` — 200 também quando a
  sandbox morreu; resposta ganha `rehydrated: bool` +
  `from_checkpoint_id?: string`.
- Novo evento ledger: `kind="sandbox_rehydrated"`, payload
  `{checkpoint_id, sandbox_id}`.
- Erros: `422 no_snapshot` só quando explicitamente pedido
  `?require_snapshot=true` (opt-in p/ automações); default = warning.

## 5. Critérios de aceite

- **Dado** conversa com turno concluído e sandbox parada/removida,
  **quando** `POST /resume`, **então** nova sandbox sobe com o workdir
  do último checkpoint e o run continua; arquivo criado pelo agente no
  turno anterior está presente.
- **Dado** conversa sem snapshot, **quando** resume, **então** workdir
  vazio + warning visível na UI (banner "workspace restaurado vazio").
- **Dado** TTL excedido, **quando** o sweep roda, **então** o dir é
  removido e resume cai no caminho sem snapshot.
- Edge: resume durante export em andamento → aguarda/usa checkpoint
  anterior; dois resumes simultâneos → um vence (lock por conversa).

## 6. Task Plan

1. `IWorkdirSnapshotStore` + `FilesystemWorkdirSnapshotStore`
   (export/import tar via `SandboxService` archive API) + testes unit.
2. Hook no auto-checkpoint do turno → export best-effort + teste.
3. Caminho de reidratação no resume (detecção sandbox morta, create,
   import, evento) + testes integração (Testcontainers: mata container,
   resume, assert arquivo).
4. Cleanup: delete de conversa + sweep TTL no maintenance worker + teste.
5. Config + docs + `.env.exemplo`.
6. UI: banner de reidratação na `ConversationPage` (i18n ×8) + teste bUnit.

## 7. Deliverables

- `src/Sandcastle.Infrastructure/Snapshots/` (store + GC)
- Resume reidratante + evento `sandbox_rehydrated`
- Banner UI + i18n ×8
- Docs env vars + testes (unit + integração Testcontainers)

## 8. Referências

- `docs/en/comparison.md` §roadmap — "resume on NEW sandbox"
- SPEC-135 (checkpoints dentro do container — motivo do export host-side)
- SPEC-43 (auto-checkpoint por turno, drift-guard)

## 9. Guardrails

- Nunca bloquear o turno no export (best-effort, timeout 30s).
- Snapshots fora do container — sem depender de volume Docker nomeado.
- Segredos da conversa **não** entram no tar (`.env`, `.git-credentials`
  filtrados; teste de regressão).
- Schema novo → EF migration + `DatabaseMigrator`.

## 10. DoD

- [ ] Resume pós-`docker rm -f` restaura arquivos e continua o run.
- [ ] Ledger registra `sandbox_rehydrated`; UI mostra banner.
- [ ] Sweep TTL + delete de conversa limpam o state dir.
- [ ] `dotnet format` clean, build 0 warnings, testes verdes.
- [ ] Docs PT/EN + `.env.exemplo` atualizados.
