# SPEC — 166 · Session insights v2 (heurística L1 + LLM L2)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `session-insights-v2` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | Epic #TBD · Issue #TBD |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário revisando conversas, **I want** insights que combinem
heurística determinística (tamanho, categoria, timeline) com análise LLM
(action items, aprendizados) **so that** eu entendo o que deu certo/errado
e o que melhorar — paridade com o `session-insights-v2` do OpenWebUiCode
(`0f82fd5`).

## 2. Contexto / escopo

`SessionInsightService` já gera análise **heurística** (outcome, issues,
dead ends, phase costs, learnings, suggested prompt) e expõe
`GET /app-conversations/{id}/insights`, `POST .../insights/reanalyze` e
`GET /insights/stats`. Falta a camada L2 (LLM) e o painel admin rico.

Faltam:

- Análise LLM L2 persistida (categoria, size tier XS–XL, timeline,
  action items) usando o modelo da conversa via `IAgentLlmProvider`
  (chat stream agregado); **sem fallback enlatado** — falha do provider →
  502 e insight permanece L1.
- `SessionInsightRecord` ganha campos L2 + migration dual.
- `GET /insights/stats` estendido: distribuição de tamanhos, categorias,
  top issues, taxa de insights com L2.
- UI: painel de insights na conversa (aba Info ou seção) + cards admin;
  i18n ×8.

Fora de escopo: dreaming-lite (aplicar action items automaticamente —
SPEC futura), export de insights.

## 3. Requisitos

- RF-001: `SessionInsightService.AnalyzeSessionAsync` passa a gerar L1
  (existente) + L2 (LLM) quando `Insights:LlmAnalysis` (default true) e
  provider disponível; L2 persiste `Category`, `SizeTier`, `TimelineJson`,
  `ActionItemsJson`, `LlmSummary`.
- RF-002: prompt L2 recebe amostra de eventos (mesma amostragem do L1,
  ≤100 eventos) + métricas L1; saída JSON validada (schema tolerante,
  fallback para L1-only em JSON inválido com `LlmAnalysisFailed=true`).
- RF-003: `reanalyze` com `?force=true` re-executa L1+L2; sem provider →
  502 (nunca valor enlatado).
- RF-004: `GET /insights/stats` estendido (size distribution, categories,
  top issues, l2 coverage) com filtros `orgId`, `days`.
- RF-005: UI — painel de insights na conversa (summary, action items,
  timeline) + cards admin em página de analytics; i18n ×8.
- RF-006: testes — unit do parse L2/schema, integração com LLM fake,
  stats estendido, bUnit do painel.

## 4. API Contract

- `GET /app-conversations/{id}/insights` → DTO estendido com campos L2.
- `POST /app-conversations/{id}/insights/reanalyze?force=true`.
- `GET /insights/stats?orgId=&days=` → estendido.

## 5. Critérios de aceite

- **Dado** conversa com eventos suficientes, **quando** gero insights,
  **então** recebo L1 + L2 persistidos e o painel mostra action items.
- **Dado** provider LLM falhando, **quando** reanaliso, **então** 502 e o
  insight anterior permanece intacto.

## 6. Task Plan

1. Migration dual + campos L2 no record/DTO.
2. `LlmInsightAnalyzer` (prompt + parse + schema tolerante).
3. Service wiring + stats estendido.
4. UI painel + admin cards + i18n ×8.
5. Testes.

## 7. Deliverables

- Insights L2 + stats + UI + testes

## 8. Referências

- OpenWebUiCode `0f82fd5` (session insights v2)
- Sandcastle `src/Sandcastle.Infrastructure/Insights/`

## 9. Guardrails

- Nunca persistir saída enlatada sobre dados do usuário.
- Custo: L2 só sob demanda (reanalyze) ou 1x por conversa (cache por
  `Conversation.UpdatedAt`).
- Amostragem de eventos limitada (≤100) para custo/latência.

## 10. DoD

- [ ] L2 implementado com provider fake; stats estendido; UI i18n ×8.
- [ ] Migration dual; testes verdes.
