# Agent run cognition and activity UX (SPEC-193)

Sandcastle exposes explicit run-cognition signals so operators can understand what the agent is optimizing for, when it is waiting, and which memory informed its decisions.

## Tools

| Tool | Behavior | Bounds |
|---|---|---|
| `goal` | Records the current objective as a durable `run_goal` event. | Objective is redacted and capped at 500 characters. |
| `wait` | Waits for elapsed time and can probe a sandbox background job. | Always clamped to 1–30 seconds; never an indefinite executor block. |
| `memory_reflect` | Returns a digest of relevant conversation/org memory and knowledge entries with provenance. | 1–10 entries, bounded excerpts, read-only. |

## Activity lifecycle

Every agent run emits exactly one canonical `analyzing` phase before the first `generating` phase. Retry/fallback paths do not duplicate `analyzing`; both tool-loop and plain-completion streaming share the same contract.

Advisor output is normalized to machine-readable severity: `nit`, `concern`, or `blocker`. The value is persisted in the `advisor` event and rendered as a severity-coded activity card.

Reloaded observations restore the persisted `tool_name`; collapsed output previews keep line-oriented diffs (`@@`, `+`, `-`) instead of flattening them, while structured observations surface counts and paths when available.

## Mobile UX

Activity cards wrap header metadata, preserve horizontal scrolling for output, and keep the humanized tool activity line readable on narrow screens. These rules match the responsive activity/insights polish introduced in SPEC-177.

---

# Cognição da run e UX de atividade do agente (SPEC-193)

O Sandcastle expõe sinais explícitos de cognição da run para que operadores entendam o objetivo do agente, quando ele está esperando e quais memórias influenciaram decisões.

## Tools

| Tool | Comportamento | Limites |
|---|---|---|
| `goal` | Registra o objetivo atual como evento durável `run_goal`. | Objetivo redigido/mascarado e limitado a 500 caracteres. |
| `wait` | Espera tempo decorrido e pode inspecionar um job em background do sandbox. | Sempre limitado a 1–30 segundos; nunca bloqueia indefinidamente. |
| `memory_reflect` | Retorna digest de memórias/conhecimento relevantes da conversa/org com provenance. | 1–10 entradas, excerpts limitados, somente leitura. |

## Ciclo de atividade

Toda run emite exatamente uma fase canônica `analyzing` antes da primeira fase `generating`. Retries/fallbacks não duplicam `analyzing`; loops com tools e completions simples seguem o mesmo contrato.

A saída do advisor é normalizada em severidade machine-readable: `nit`, `concern` ou `blocker`. O valor é persistido no evento `advisor` e renderizado como card de atividade codificado por severidade.

Observações recarregadas restauram o `tool_name` persistido; previews colapsados preservam linhas de diff (`@@`, `+`, `-`) em vez de achatá-las, e observações estruturadas mostram contagens/caminhos quando disponíveis.

## UX mobile

Cards de atividade quebram metadados do cabeçalho, preservam scroll horizontal para output e mantêm a linha humanizada da tool legível em telas estreitas, consistentes com o polimento responsivo da SPEC-177.
