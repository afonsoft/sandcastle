# SPEC — 167 · Model routing v2 (pools adaptativos + fallback)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `model-routing-v2` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #499 · Epic #492 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** operador do Sandcastle, **I want** pools de modelos por task
com pesos adaptativos, custo por token e fallback automático **so that**
runs usem o modelo mais barato que resolve e não caiam quando um
provider falha — paridade com `model-routing-v2` do OpenWebUiCode
(`0b8a442`, `70a71ee`).

## 2. Contexto / escopo

Hoje `ModelRouter` (SPEC-149) escolhe por role estático; `AdaptiveCostRouter`
existe em `OmniRoute/` como **código morto** (modelos hardcoded, 0 usos).
Não há pools por task, nem fallback entre providers, nem aprendizado de
qualidade.

Faltam:

- Pools de modelos por task (`chat`, `plan`, `compact`, `embedding`,
  `title`) com **ordem de preferência** e **peso adaptativo** (sucesso/
  custo acumulado por modelo, janela deslizante).
- Fallback automático: falha de provider (429/5xx/timeout) → próximo do
  pool com backoff; circuito por modelo.
- Custo estimado por run (tokens in/out × preço configurável por modelo).
- **Remover/substituir** `AdaptiveCostRouter` morto pela implementação
  real (ou deletar se redundante).

Fora de escopo: fine-tuning, prompt caching, providers não-OpenAI-compat.

## 3. Requisitos

- RF-001: `ModelPoolRecord`/config `ModelRouting:Pools:{task}` — lista de
  `{provider, model, weight?, priceIn?, priceOut?}`; pool default =
  comportamento atual (role→model).
- RF-002: `AdaptiveRouter` escolhe modelo por task: score =
  `weight_base × quality_ema × cost_factor`; quality_ema atualizado por
  run (sucesso/falha, latência); persistido em `ModelStatsRecord`
  (migration dual) com janela de 50 runs.
- RF-003: fallback chain — exceção transitória do provider → próximo do
  pool (máx `ModelRouting:MaxFallbacks` = 2), com `model_fallback` evento;
  erro permanente (auth) → propaga.
- RF-004: `GET /model-routing/pools` + `PUT` (admin) expõem pools e stats;
  `RunRecord`/`UsageRecord` ganham `ModelUsed`, `CostUsd` estimado.
- RF-005: deletar `AdaptiveCostRouter` morto ou refatorar para usar
  `ModelPool` (decisão documentada na SPEC).
- RF-006: testes — unit do score/fallback/circuit, integração com 2
  providers fake (fallback + stats), migration dual.

## 4. API Contract

- `GET/PUT /api/v1/admin/model-routing/pools` (admin).
- `GET /api/v1/model-routing/stats?task=` → health por modelo.
- `UsageRecord` DTO ganha `model`, `cost_usd`.

## 5. Critérios de aceite

- **Dado** pool com 2 modelos e o 1º falhando com 500, **quando** run
  inicia, **então** fallback para o 2º com evento `model_fallback`.
- **Dado** 50 runs com o modelo A falhando e B sucedendo, **quando** nova
  run, **então** B é preferido (weight adaptativo).

## 6. Task Plan

1. Records/config + migration dual.
2. `AdaptiveRouter` + `ModelStatsStore`.
3. Fallback + circuit no dispatch LLM.
4. Endpoints admin + stats; remover dead code.
5. Testes.

## 7. Deliverables

- Router adaptativo + fallback + custo + endpoints + testes

## 8. Referências

- OpenWebUiCode `0b8a442` (adaptive/fusion pools), `70a71ee`
- Sandcastle `src/Sandcastle.Infrastructure/Agent/Routing/ModelRouter.cs`,
  `src/Sandcastle.Infrastructure/OmniRoute/AdaptiveCostRouter.cs` (dead)

## 9. Guardrails

- Nunca trocar de modelo no meio de uma run (só entre runs/turnos LLM).
- Custo estimado é informativo; billing real fica fora de escopo.
- Pools vazios → fallback para role estático atual (compat).

## 10. DoD

- [ ] Pools adaptativos + fallback testados; dead code resolvido.
- [ ] Migration dual; endpoints admin; docs.
