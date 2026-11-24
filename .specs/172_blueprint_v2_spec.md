# SPEC — 172 · Blueprint v2 (fases, knowledge, secrets, test)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `blueprint-v2` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #522 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** operador/usuário com repos grandes, **I want** blueprint com
fases `initialize`/`maintenance`, notas de knowledge injetadas no agente
e endpoint de teste **so that** o sandbox sobe pronto na primeira
conversa, re-usa provisionamento via snapshot e o blueprint é
validável — paridade com o `environment.yaml` do Devin aplicado ao
`.agents/blueprint.yaml` (canonical; `.devin`/`.claude` compat).

## 2. Contexto / escopo

`BlueprintService` (SPEC-17) resolve `sandcastle.yaml` → org inline →
auto-detect e roda SDK installers + `.openhands/setup.sh` + env — tudo
a cada sandbox. Falta: separação de fases (setup pesado vs refresh),
reuso via snapshot, knowledge para o agente, secrets, validação prévia
e surfacing no Client.

Mudanças:

- **Canonical path**: `.agents/blueprint.yaml` passa a ser o arquivo
  preferido do repo; precedência `.agents/blueprint.yaml` →
  `sandcastle.yaml` → `.devin/blueprint.yaml` (compat) → org inline →
  auto-detect. `.openhands/setup.sh` segue como compat de `setup:`.
- **Fases**: `initialize:` (1× por snapshot — provisiona imagem/SDK/deps
  pesadas) e `maintenance:` (a cada boot — restore/sync). `setup:`
  atual = maintenance; `initialize` resulta em snapshot reutilizável por
  `blueprint_hash` (SPEC-43 sandbox snapshots / warm pool).
- **`knowledge:`** blocos nomeados injetados via
  `IAgentPromptContributor` (`<blueprint_knowledge>`) no system prompt.
- **`secrets:`** lista de nomes resolvidos do vault org/user no
  provision — nunca logados (SecretRedactor já cobre).
- **Blueprint test**: `POST /api/v1/blueprints/test` sobe sandbox
  efêmero com um blueprint YAML, roda initialize + maintenance e devolve
  status/fase/log tail — workflow "test → fix → persist" (estilo
  `devin_blueprint_test`).

Fora de escopo: build de imagem custom (campo `image:` continua
pin/pull), marketplace de blueprints.

## 3. Requisitos

- RF-001: `BlueprintSpec` v2 — `Initialize`, `Maintenance`, `Env`,
  `Secrets`, `Knowledge[]`, `Image`, `Sdks`; parser aceita schema v1
  (`setup:` → maintenance) e v2; canonical `.agents/blueprint.yaml` com
  compat `.devin` lido como fallback.
- RF-002: `blueprint_hash` (sha256 do spec normalizado) gravado no
  sandbox record; initialize só roda quando o hash difere do snapshot
  base; maintenance roda em todo start (incl. resume).
- RF-003: knowledge injetado via contributor novo
  (`BlueprintKnowledgeContributor`, Order após repo context) —
  truncado a `Blueprint:MaxKnowledgeBytes` (default 8 KB).
- RF-004: secrets — nomes em `secrets:` resolvem valores do vault do
  owner/org no provision; ausência = warning no blueprint log;
  valores nunca serializam no record (env mascarado já existe).
- RF-005: `POST /api/v1/blueprints/test` — `{repo?, yaml}` → sandbox
  efêmero (auto-destroy), retorna `{status, phases:[{name,exit,log_tail}],
  duration_ms}`; `POST /app-conversations/{id}/blueprint/rerun` re-executa
  maintenance na conversa.
- RF-006: Client — `BlueprintStatus` + log tail na conversa
  (conversation ops panel / workspace config) + resultado do test.
- RF-007: testes — unit do parser (v1/v2/precedência/canonical),
  hash, contributor, secrets; integração do test endpoint com engine
  fake; migration dual.

## 4. API Contract

- `POST /api/v1/blueprints/test` (auth) — schema acima.
- `POST /api/v1/app-conversations/{id}/blueprint/rerun`.
- `Sandbox` DTO ganha `blueprint_hash`, `blueprint_phases`.
- Divergência: schema v2 estende `sandcastle.yaml` upstream — documentar.

## 5. Critérios de aceite

- **Dado** repo com `.agents/blueprint.yaml` (initialize+maintenance),
  **quando** 2ª conversa do mesmo repo+hash inicia, **então** initialize
  é pulado via snapshot e o boot fica só com maintenance.
- **Dado** `knowledge:` definido, **então** o system prompt da run
  contém `<blueprint_knowledge>`.
- **Dado** YAML inválido no test endpoint, **então** retorno `failed`
  com fase e log tail — sem derrubar nada.

## 6. Task Plan

1. Schema/parser v2 + precedência `.agents` + compat + hash.
2. Fases + snapshot reuse + secrets + knowledge contributor.
3. Endpoints test/rerun + DTOs + OpenAPI golden.
4. Client surfacing.
5. Migration dual + testes.

## 7. Deliverables

- Blueprint v2 com fases/knowledge/secrets/test + reuse via snapshot

## 8. Referências

- Devin `environment.yaml` (docs.devin.ai/onboard-devin/environment-yaml,
  conceito de fases/knowledge); `.devin/blueprint.yaml` do OpenWebUiCode
- Sandcastle `BlueprintService.cs`, `WorkspaceProvisioner`,
  `SandboxSnapshotEndpoints`, SPEC-17/43/154; `.agents/blueprint.yaml`
  (canonical deste repo)

## 9. Guardrails

- Falha de blueprint nunca derruba a conversa (status `failed` + log).
- Secrets resolvidos só no provision; nunca em log/record serializado.
- Compat: repos com só `sandcastle.yaml`/`.openhands/setup.sh` seguem
  funcionando sem migração.

## 10. DoD

- [ ] Fases + knowledge + secrets + test endpoint entregues.
- [ ] Snapshot reuse por hash validado; migration dual verde.
