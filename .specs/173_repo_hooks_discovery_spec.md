# SPEC — 173 · Hooks descobertos no repositório (.agents canônico)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `repo-hooks-discovery` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #523 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** dono de repo, **I want** hooks de ciclo de vida declarados no
próprio repositório (`.agents/hooks.json`) executados dentro do sandbox
em `PreToolUse`/`PostToolUse`/`UserPromptSubmit` **so that** o agente
respeita automação versionada com o código — paridade com o
`HookConfigLoader` multi-fonte do OpenWebUiCode, com `.agents/` como
diretório canônico e `.devin`/`.claude` lidos como compat.

## 2. Contexto / escopo

Sandcastle tem hooks (SPEC-11) porém só DB-driven por conversa — nada é
descoberto no workspace. OpenWebUiCode carrega `.openwebui/hooks.json`,
`.devin/hooks.v1.json`, `.devin/config.json#hooks`,
`.claude/settings*.json` e `~/.config/...`, repo > usuário.

Mudanças:

- `RepoHookLoader`: lê do workspace (via sandbox `/files` ou bind-mount)
  `.agents/hooks.json` + `.agents/hooks/*.json` (canônico); compat
  `.devin/hooks.v1.json`, `.devin/config.json#hooks`,
  `.claude/settings*.json#hooks` (opcional, config
  `Hooks:CompatDirs` default on). Usuário: `~/.agents/hooks.json` no
  perfil do sandbox owner.
- Precedência repo > usuário; merge com hooks DB existentes (DB hooks
  rodam depois dos file hooks no mesmo evento).
- Eventos MVP: `PreToolUse`, `PostToolUse`, `UserPromptSubmit` —
  matcher por glob de tool (`"Bash|File*"`); ação `command` executada no
  sandbox com `timeout_sec`, output cap 4 KB, env `TOOL_NAME`,
  `TOOL_ARGS_JSON`, `MATCHER`.
- Semântica: `PreToolUse` exit≠0 ou JSON `{"decision":"deny","reason"}`
  → tool call vetada com erro ao modelo; `PostToolUse` exit≠0 → warning
  no resultado; `UserPromptSubmit` stdout vira contexto extra
  (`<hook_context>`); JSON `{"decision":"block"}` recusa a mensagem.
- Eventos `hook_fired`/`hook_denied` no stream + seção "Repo hooks" em
  `GET /{id}/tools` ou painel de hooks (HookEndpoints já lista DB hooks —
  estender com origem `repo`/`user`).

Fora de escopo: matchers por regex de args (v2), hooks assíncronos,
`SessionStart`/`Stop` (seguir em SPEC própria se pedirem).

## 3. Requisitos

- RF-001: loader multi-fonte com schema `{hooks|*:{Event:[{matcher,
  hooks:[{type:"command",command,timeout_sec?}]}]}}` (formato
  Devin/Claude-compat); chaves desconhecidas = warning não fatal.
- RF-002: `FileHookRunner` executa via `ISandboxExecutor` no workdir da
  conversa; timeout default 30s (max 120s); falha do runner = fail-open
  com warning (exceto `deny`/`block` explícito).
- RF-003: integração no `AgentRunner`/`PermissionedToolRegistry` —
  PreToolUse antes do approval tier; deny registra `hook_denied`.
- RF-004: `HookRecord`/`HookExecution` ganha `source`
  (`db|repo|user`) e `file_path`; endpoints listam origem.
- RF-005: config `Hooks:RepoFiles` (lista, default canônico+compat),
  `Hooks:UserFile`, `Hooks:Enabled` global.
- RF-006: testes — unit do loader (schema, glob, precedência, malformed),
  runner com executor fake (deny/block/contexto), integração no runner.

## 4. API Contract

- `GET /api/v1/app-conversations/{id}/hooks` estendido: `source`,
  `file_path` por hook; execuções já existentes ganham `source`.
- Sem endpoints novos; schema de arquivo documentado em
  `docs/features` (`.agents/hooks.json`).

## 5. Critérios de aceite

- **Dado** `.agents/hooks.json` com PreToolUse matcher `Bash` que
  retorna deny, **quando** o agente chama bash, **então** a call é
  vetada com a razão no tool result e `hook_denied` é emitido.
- **Dado** `.devin/hooks.v1.json` legado, **então** é executado com a
  mesma semântica (compat) sem precisar renomear.
- **Dado** hook ausente/malformado, **então** a run segue com warning.

## 6. Task Plan

1. `RepoHookLoader` (schema/glob/precedência/canônico+compat).
2. `FileHookRunner` no sandbox + semântica deny/block/context.
3. Merge DB+file hooks no `AgentRunner`; eventos/records.
4. Endpoints/UI origem + config + docs.
5. Testes.

## 7. Deliverables

- Hooks de repo `.agents` (compat `.devin`/`.claude`) executando no
  sandbox nos 3 eventos

## 8. Referências

- OpenWebUiCode `ChatTools/HookConfigLoader.cs`,
  `LifecycleHookService.cs`, `CommandHookRunner.cs`,
  `PromptHookEvaluator.cs`
- Sandcastle `Domain/Hooks/HookConfig.cs`, `HookEndpoints.cs`,
  `PermissionedToolRegistry.cs` (SPEC-11)

## 9. Guardrails

- File hooks rodam dentro do sandbox com as mesmas restrições de egresso
  — nunca no host.
- `deny`/`block` é a única saída que altera controle — stdout/exit
  comum nunca injeta tool calls.
- Compat é read-only: nunca escrevemos em `.devin`/`.claude`.

## 10. DoD

- [ ] `.agents/hooks.json` funciona nos 3 eventos; compat lida.
- [ ] Precedência e merge com DB hooks testados; eventos emitidos.
