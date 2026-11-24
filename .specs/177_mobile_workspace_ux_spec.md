# SPEC — 177 · Mobile workspace UX (touch, panes, safe-area)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `mobile-workspace-ux` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #527 · Epic #520 |
| Scope | `src/Sandcastle.Client` + `tests/` |

## 1. User Story

**As a** usuário no celular, **I want** chat, workspace, terminal e
repo picker usáveis em viewport pequeno com targets touch, panes
colapsáveis e safe-area **so that** acompanho e intervenho em runs de
qualquer lugar — paridade com o "Mobile workspace" do OpenWebUiCode
(touch targets + collapsing chips + picker acessível).

## 2. Contexto / escopo

`MainLayout.razor` já tem drawer mobile (topbar + `mobile-open`), mas
a conversa é 3-pane Devin-style (chat | workspace | terminal/IDE) sem
tratamento mobile: panes side-by-side estouram viewport, composer sem
safe-area, cards/ops panel sem target ≥44px, Monaco/xterm ilegíveis
sem zoom/pan.

Mudanças:

- Layout de conversa mobile: panes viram tabs/stack com bottom bar
  (`Chat`/`Workspace`/`Terminal`/`Files`) < `md` breakpoint; swipe ou
  tabs alternam; estado persiste por sessão.
- Touch targets ≥ 44×44 em ações de mensagem, cards, conversation ops,
  repo picker, surfaces; chips de status colapsam para ícone+dot.
- Composer: `env(safe-area-inset-*)`, keyboard-open não cobre input
  (VisualViewport), long-press/secondary actions acessíveis.
- IDE/terminal mobile: read-only default + "expand" fullscreen;
  Monaco font ≥12px, xterm cols adaptados; file tree vira lista.
- PWA: manifest/meta apple-touch + `viewport-fit=cover` (verificar);
  iOS momentum scroll nos panes.
- Auditoria: páginas Chat/Workspace/Automations/Settings mapeadas;
  baseline CSS/a11y nos drift guards (`check-css-classes`,
  `check-form-a11y`).

Fora de escopo: gestos avançados (pull-to-refresh), app nativo,
edição completa no Monaco mobile (read-only + fullscreen basta).

## 3. Requisitos

- RF-001: conversa mobile com panes em tabs + bottom nav < `md`;
  altura estável (`dvh`/`svh`), sem overflow horizontal.
- RF-002: targets ≥44px auditados nas superfícies da conversa; chips
  colapsáveis; aria-labels completos (a11y guard).
- RF-003: composer safe-area + keyboard handling; actions de mensagem
  acessíveis via menu (não só hover).
- RF-004: workspace/terminal read-only mobile + expand; lista de arquivos
  otimizada; surfaces abrem em nova aba.
- RF-005: `viewport-fit=cover` + meta PWA revisados; i18n das novas
  strings nas 8 locales.
- RF-006: testes — bUnit de layout/tabs, drift guards
  (css-classes/form-a11y/i18n) verdes, baseline drift atualizado só
  com o que mudar intencionalmente.

## 4. API Contract

- Sem mudança de API. Mudanças confinadas a `.razor`/CSS/JS interop
  (VisualViewport) do Client.

## 5. Critérios de aceite

- **Dado** viewport 390×844, **quando** abro uma conversa, **então**
  navego entre chat/workspace/terminal por tabs sem overflow e envio
  mensagem com o teclado aberto.
- **Dado** qualquer ação de conversa, **então** target ≥44px e nome
  acessível (a11y guard verde).
- **Dado** iPhone com notch, **então** composer e bottom nav respeitam
  safe-area.

## 6. Task Plan

1. Layout tabs/bottom-nav + safe-area + dvh.
2. Touch targets + chips + menu de ações.
3. IDE/terminal mobile read-only + expand.
4. PWA meta + i18n + auditoria de páginas.
5. bUnit + drift guards + baseline.

## 7. Deliverables

- Conversa/mobile completa com tabs, touch e safe-area

## 8. Referências

- OpenWebUiCode "Mobile workspace" (README agent features);
  `ChatView.razor` mobile chips/repo picker
- Sandcastle `MainLayout.razor` (drawer), SPEC-136 nav-polish,
  `tools/check-form-a11y.py`, `tools/check-css-classes.py`

## 9. Guardrails

- Nenhuma regressão desktop (abas só < `md`; layout atual intacto).
- Read-only mobile nunca edita arquivos por acidente; expand é opt-in.
- Drift baselines só atualizam com delta revisado.

## 10. DoD

- [ ] Conversa usável E2E em 390px; a11y/css/i18n guards verdes.
