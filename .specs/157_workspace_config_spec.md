# SPEC — 157 · Workspace de configuração (modelos / prompts / arquivos / tools)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `workspace-config` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #455 · Epic #451 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** admin/power-user, **I want** uma área `/workspace` para criar e
editar artefatos reutilizáveis do agente — prompts salvos, presets de
modelo (combos), arquivos de contexto e tool configs — **so that**
configuro o ambiente do agente uma vez e reuso em qualquer conversa,
como o `/workspace` do OpenWebUiCode (prompts/models/files/knowledge/
tools/skills com editor por item).

## 2. Contexto / escopo

Sandcastle tem peças soltas: ModelSelector por conversa, skills/MCPs em
settings, knowledge própria. Falta a camada de **artefatos
reutilizáveis versionáveis pelo usuário**:

- **Prompts salvos**: CRUD de prompts nomeados (corpo markdown, hint
  de uso) → slash-command `/` e picker no `ChatComposer`.
- **Model presets**: nome + `{provider, model, temperature?, max_tokens?,
  agent_type?}` → aparecem no `ModelSelector` acima dos modelos crus;
  preset default por usuário.
- **Context files**: arquivos de texto/markdown do usuário (ex.:
  guidelines da equipe) → anexáveis à conversa (viram contexto do
  system prompt, seção `user_context_files`).
- **Tools**: nesta fase apenas a listagem read-only dos MCP tools
  disponíveis (descoberta já existe) — edição fica no Customize Hub.

Fora de escopo: sharing de artefatos entre usuários, versionamento git,
execução de tools pelo workspace.

## 3. Requisitos

- RF-001: entidade `WorkspaceItem {id, owner_id, org_id?, kind
  (prompt|model_preset|context_file), name, body jsonb, created_at,
  updated_at}` + EF migration (SQLite + Postgres) + audit.
- RF-002: CRUD `GET|POST|PUT|DELETE /api/v1/workspace/items[/{id}]`
  (`?kind=` filtro); ownership do usuário; itens de org visíveis a
  membros (read) e editáveis por admin.
- RF-003: rota `/workspace` (nav item) + tabs por kind + página de
  edição `/workspace/{kind}/{id}` (form simples + preview do prompt);
  i18n ×8.
- RF-004: integração — picker de prompts no composer (insere corpo),
  presets no `ModelSelector` (grupo "Presets" no topo), context files
  anexáveis via `@` mention ou painel da conversa.
- RF-005: lista read-only de MCP tools na tab Tools (nome, servidor,
  descrição — do catálogo já publicado).

## 4. API Contract

- `GET /api/v1/workspace/items?kind=prompt` → `[{id,kind,name,scope,
  updated_at}]`
- `POST /api/v1/workspace/items {kind,name,body}` → 201 `{id}`
- `PUT /api/v1/workspace/items/{id}` / `DELETE` → 204/404/403
- `body` por kind: prompt `{text,description?}` · preset
  `{provider,model,agent_type?,temperature?,max_tokens?}` ·
  context_file `{text,filename}`.
- `GET /api/v1/workspace/tools` → catálogo read-only de MCP tools.

## 5. Critérios de aceite

- **Dado** preset "reviewer-sonnet" criado, **quando** abro o
  ModelSelector, **então** ele aparece no grupo Presets e aplica
  provider+model+agent_type de uma vez.
- **Dado** prompt salvo, **quando** seleciono no composer, **então** o
  corpo vai pro input (editável antes de enviar).
- **Dado** context file anexado, **quando** a run inicia, **então** o
  texto entra na seção `user_context_files` do system prompt (visível
  no ledger/prompt log).
- **Dado** item de outro usuário, **quando** PUT/DELETE, **então** 403;
  item de org read-only para não-admin.

## 6. Task Plan

1. Entity + migration ×2 + CRUD endpoints + ownership + testes.
2. `WorkspacePage` (tabs + lista) + `WorkspaceItemPage` (editor) + i18n.
3. Presets no ModelSelector + prompts no composer + context file no
   system prompt + testes.
4. Tab Tools read-only + golden OpenAPI + docs.

## 7. Deliverables

- `WorkspaceItem` + endpoints + 2 páginas + integrações composer/model/
  prompt + i18n ×8 + testes

## 8. Referências

- OpenWebUiCode `Pages/Workspace.razor` + `Workspace/{kind}/{id}` editor
- Sandcastle `ChatComposer` (@/slash), `ModelSelector`, system-prompt
  contributors (SPEC-90)

## 9. Guardrails

- `body` validado por kind (JSON schema por tipo); tamanho máx 64KB.
- Context file truncado a 8KB no system prompt (sinalizar `truncated`).
- Audit log em create/update/delete; sem secret no body (validação
  `sk-`/token patterns → 422).

## 10. DoD

- [ ] CRUD + permissões pessoal/org verificadas em teste.
- [ ] 3 integrações funcionais (composer, model, system prompt).
- [ ] i18n ×8, golden OpenAPI, format/build/test verdes.
