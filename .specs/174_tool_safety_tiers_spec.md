# SPEC — 174 · Tool tiers, risco de comando, arg repair e paralelismo

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `tool-safety-tiers` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #524 · Epic #520 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** operador, **I want** tiers declarativos `Read/Write/Exec`
fail-closed por tool, classificação de risco de comandos, reparo de
args malformados e execução paralela das tools seguras **so that** o
agente é mais rápido sem abrir mão de aprovação — paridade com
`ApprovalTier`/`SmartPermissionClassifier`/`ToolArgRepair`/
`SafeInParallel` do OpenWebUiCode.

## 2. Contexto / escopo

`PermissionedToolRegistry` + `SmartPermissionJudge` cobrem approval
por tool rígida (`Risky` bool), mas: sem tier granular, sem
classificador de comando bash (destructive patterns), args malformados
derrubam a call sem reparo, e batch de tool calls é sequencial.

Mudanças:

- `AgentTool` ganha `Tier {Read,Write,Exec}` (default Exec fail-closed
  para as atuais `Risky`; Read para file_read/grep/glob/web_fetch etc.).
  `BuiltinTools:Disabled` (csv) desativa tools no registry.
- `CommandRiskClassifier`: bash/finish/git classificam o comando
  (rm -rf, force-push, drop, chmod -R /, secrets paths) → eleva para
  Exec mesmo se a tool for Write; heurística local, sem LLM.
- `ToolArgRepair`: unwrap de JSON double-encoded (até 2 níveis),
  trim/comentários, coerção de tipos triviais — port do
  `NormalizeArgs`/`ToolArgRepair`.
- `SafeInParallel` por tool; batch de tool calls do mesmo turno executa
  paralelas com `Agent:MaxParallelTools` (default 4); resultado
  preserva ordem do array do provider.
- Overrides por usuário (`ToolPermissions` existente?) relaxam mas
  nunca enfraquecem um deny de tier Exec — paridade permissions v2.

Fora de escopo: policy por org admin (SPEC futura), risk classifier
com LLM (SmartPermissionJudge já cobre o caminho lento).

## 3. Requisitos

- RF-001: `ApprovalTier` no `AgentTool`/registry; `PermissionedToolRegistry`
  usa tier + override do usuário; tools sem tier declarado = Exec.
- RF-002: `CommandRiskClassifier` (puro) → `{Read,Write,Exec}` por
  comando; cobertura das classes: delete/fs, git destructive, perm,
  network egress, package install, secret paths.
- RF-003: `ToolArgRepair` aplicado antes do parse de args no dispatch —
  JSON malformado vira ToolResult de erro (nunca exceção da run).
- RF-004: dispatch paralelo — calls `SafeInParallel` do mesmo turno
  rodam com cap `MaxParallelTools`; demais sequenciais na ordem;
  cancelamento propaga.
- RF-005: config `Agent:MaxParallelTools`, `BuiltinTools:Disabled`
  documentadas.
- RF-006: testes — unit do classifier/repair/tier; integração batch
  paralelo com tools fake; deny de Exec não contornável por override.

## 4. API Contract

- Sem endpoints novos. `GET /{id}/tools` DTO ganha `tier`,
  `safe_in_parallel` por tool (UI de permissions).
- Eventos: `tool_call_denied` (tier), `tool_args_repaired` (diagnóstico).

## 5. Critérios de aceite

- **Dado** turn com 3 calls `file_read` + 1 `bash`, **então** as 3
  leituras rodam em paralelo e bash sequencial depois.
- **Dado** `bash` com `rm -rf /`, **então** classificado Exec e exige
  aprovação mesmo com override de usuário.
- **Dado** args `"{\"path\":\"/a\"}"` (string-encoded), **então** a tool
  executa com args reparados e `tool_args_repaired` é emitido.

## 6. Task Plan

1. Tier no modelo + registry + defaults por tool.
2. `CommandRiskClassifier` + `ToolArgRepair`.
3. Dispatch paralelo com cap.
4. Overrides + DTO/eventos + config.
5. Testes.

## 7. Deliverables

- Tiers + classifier + repair + dispatch paralelo

## 8. Referências

- OpenWebUiCode `ChatTools/PermissionRuleset.cs`,
  `ToolApprovalPolicy.cs`, `CommandRiskClassifier.cs`,
  `ToolCallRiskClassifier.cs`, `ToolArgRepair.cs`,
  `BuiltinToolRegistry.NormalizeArgs`, `SafeInParallel`
- Sandcastle `ToolRegistry.cs`, `PermissionedToolRegistry.cs`,
  `AgentRunner.DispatchToolCallsAsync`

## 9. Guardrails

- Tier Exec é fail-closed: override de usuário nunca rebaixa deny.
- Parallel só em tools declaradas `SafeInParallel` (read-only/side-
  effect-free); ordem dos resultados preservada.
- Repair nunca altera semântica — só unwrapping/coerção estrutural.

## 10. DoD

- [ ] Tiers aplicados; batch paralelo medido nos testes.
- [ ] Classifier/repair cobertos; eventos emitidos.
