# SPEC — 169 · Plan mode v2 (plan approval com diff e iteração)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `plan-mode-v2` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #501 · Epic #492 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário em modo plan, **I want** revisar o plano com diff das
mudanças propostas e iterar com comentários antes de aprovar **so that**
nada é executado sem minha validação explícita — paridade com
`plan-mode-v2` do OpenWebUiCode (`c0452b5`).

## 2. Contexto / escopo

Sandcastle já tem modos de permissão e notion de "plan" em conversas
(SPECs anteriores), mas o fluxo de aprovação é binário (aprovar/rejeitar).
OpenWebUiCode entregou: plano como artefato versionado, diff visual das
mudanças propostas, comentários por item, "approve with changes",
reattach de plano a nova run.

Faltam:

- `PlanArtifact` persistido por conversa: itens `{id, title, files[],
  rationale, status: pending|approved|rejected|modified}` + `PlanVersion`
  (incrementa a cada revisão).
- Endpoint `POST /app-conversations/{id}/plan` (cria/atualiza),
  `POST .../plan/items/{itemId}/decision`, `GET .../plan`.
- Transição plan→execute só quando todos os itens `approved` (ou
  `allow-readonly` preset da SPEC-162); rejeição volta ao agente com
  feedback.
- UI: painel de plano com diff por arquivo, checkboxes por item,
  comentários, botão "Approve & execute".
- i18n ×8.

Fora de escopo: merge de planos entre conversas, planos multi-agente
paralelos.

## 3. Requisitos

- RF-001: `PlanRecord` + `PlanItemRecord` (migration dual);
  `PlanVersion` monotônico; items com `FilesJson`, `Rationale`,
  `Status`, `CommentsJson`.
- RF-002: API — `GET /app-conversations/{id}/plan`,
  `PUT /app-conversations/{id}/plan` (substitui itens, incrementa
  versão), `POST .../plan/items/{id}/decision` (approve/reject/modify +
  comment). Transições validadas (só dono/org; conversation em modo plan).
- RF-003: execução — `POST /app-conversations/{id}/plan/execute` requer
  100% itens approved; cria run com `ApprovalPreset` derivado
  (`allow` se tudo approved; `plan` se algum rejected volta ao agente).
- RF-004: diff — endpoint `GET .../plan/diff?file=` retorna unified diff
  das mudanças propostas (vs branch base) para visualização.
- RF-005: UI — painel Plan na conversa: lista de itens, diff viewer,
  decisão por item, comentários, execute; estados vazios/loading; i18n ×8.
- RF-006: testes — unit transições de estado, integração API (auth,
  409 em estado inválido), bUnit painel, diff endpoint.

## 4. API Contract

- `GET/PUT /api/v1/app-conversations/{id}/plan`
- `POST /api/v1/app-conversations/{id}/plan/items/{itemId}/decision`
- `POST /api/v1/app-conversations/{id}/plan/execute`
- `GET /api/v1/app-conversations/{id}/plan/diff?file=...`
- DTOs snake_case; `409 plan_not_ready` quando execute sem aprovação total.

## 5. Critérios de aceite

- **Dado** plano com 3 itens (2 approved, 1 pending), **quando** execute,
  **então** 409 `plan_not_ready`.
- **Dado** plano 100% approved, **quando** execute, **então** run inicia
  com preset `allow` e evento `plan_executed`.
- **Dado** item rejeitado com comentário, **quando** agente recebe
  feedback, **então** próxima revisão do plano incrementa versão.

## 6. Task Plan

1. Records + migration dual.
2. API endpoints + state machine.
3. Diff endpoint.
4. UI painel + i18n ×8.
5. Testes.

## 7. Deliverables

- Plan mode v2 end-to-end + testes

## 8. Referências

- OpenWebUiCode `c0452b5` (plan mode v2)
- Sandcastle modes/permissions existentes, `AgentPermissionRule`

## 9. Guardrails

- Execute nunca acontece sem aprovação explícita de todos os itens.
- Planos são imutáveis após execução (nova revisão = nova versão/run).
- Diff é read-only e respeita permissões do usuário.

## 10. DoD

- [ ] State machine + API + UI testados; i18n ×8; migration dual.
