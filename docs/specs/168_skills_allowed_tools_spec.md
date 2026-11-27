# SPEC — 168 · Skills com allowed-tools (escopo de tools por skill)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `skills-allowed-tools` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #500 · Epic #492 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** autor de skills/plugins, **I want** declarar no frontmatter do
SKILL.md quais tools a skill pode usar **so that** a injeção da skill no
prompt também restrinja/escopo as tools disponíveis — paridade com
`skills-allowed-tools` do OpenWebUiCode (`5bdae49`).

## 2. Contexto / escopo

Skills hoje (`SkillRecord` + loader) injetam instruções no system prompt,
mas não há vínculo skill→tools. O usuário pode querer que uma skill de
"docs" só use `file_read`/`web_fetch`, ou que uma skill de "deploy" tenha
acesso a `bash`.

Faltam:

- Frontmatter `allowed-tools` (lista de nomes de tools) no SKILL.md;
  parse no loader + persistência (`SkillRecord.AllowedToolsJson`).
- Enforcement no `AgentRunner`: quando a skill está ativa na run, tools
  fora da lista retornam erro "not allowed by skill X" (deny-by-default
  só se a skill declarar; ausência de `allowed-tools` = sem restrição,
  compat).
- UI: editor de skill mostra/edita `allowed-tools`; autocomplete com
  nomes de tools registradas.
- API: `SkillDto` ganha `allowed_tools`.

Fora de escopo: allowed-tools por plugin inteiro, herança/merge entre
skills (primeira versão = união das skills ativas; se qualquer skill
restringe, interseção? — **decisão: união das permitidas; skill sem
allowed-tools não restringe**).

## 3. Requisitos

- RF-001: parser de frontmatter aceita `allowed-tools: ["a","b"]` ou
  string separada por vírgula; nomes validados contra catálogo de tools
  (warning em nomes desconhecidos, não erro).
- RF-002: `SkillRecord.AllowedToolsJson` + migration dual; DTO/API expõe.
- RF-003: `ToolPermissionEvaluator` (ou extensão do existente) recebe
  skills ativas da run e aplica união das `allowed-tools`; tool fora →
  `ToolResult` erro `tool_blocked_by_skill` + evento diagnóstico.
- RF-004: UI editor de skill com campo `allowed-tools` (multi-select com
  tools registradas + texto livre); i18n ×8.
- RF-005: testes — unit parser, unit evaluator (união/restrição/sem
  restrição), integração runner com skill fake, bUnit editor.

## 4. API Contract

- `SkillDto` ganha `allowed_tools: string[]`.
- Erro de tool: `{ "error": "tool_blocked_by_skill", "skill": "..." }`.

## 5. Critérios de aceite

- **Dado** skill com `allowed-tools: [file_read]`, **quando** agente tenta
  `bash`, **então** recebe erro `tool_blocked_by_skill` e evento.
- **Dado** skill sem `allowed-tools`, **quando** qualquer tool, **então**
  comportamento atual (sem restrição).

## 6. Task Plan

1. Parser frontmatter + record + migration.
2. Evaluator + integração no dispatch de tools.
3. API DTO + UI editor + i18n ×8.
4. Testes.

## 7. Deliverables

- allowed-tools end-to-end + testes

## 8. Referências

- OpenWebUiCode `5bdae49` (skills allowed-tools)
- Sandcastle `src/Sandcastle.Infrastructure/Persistence/Records.cs`
  (`SkillRecord`), loader de skills, `AgentRunner`

## 9. Guardrails

- Nunca quebrar skills existentes (sem allowed-tools = irrestrito).
- Enforcement é no dispatch de tools (não no prompt) — não confia no LLM.
- União (não interseção) quando múltiplas skills ativas; documentado.

## 10. DoD

- [ ] Restrição funcionando com testes; UI + i18n; migration dual.
