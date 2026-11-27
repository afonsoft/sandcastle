# Deep dive — OpenWebUiCode × OpenHands × OpenHands-Cloud × Devin Cloud

Data: 2026-11-27. Fontes analisadas localmente:
`afonsoft/OpenWebUiCode`, `afonsoft/OpenHands` e
`OpenHands/OpenHands-Cloud` (clone shallow). Devin Web/Cloud é referência
visual/produto externa; OpenHands-Cloud é **Polyform Free Trial**, então
serve apenas como referência conceitual — nenhum código/chart deve ser
portado.

## 1. Sumário executivo

O Sandcastle já cobre o núcleo de cloud agents: conversas, sandbox,
eventos V1, plan/build, MCP, skills, automations, conhecimento, insights,
routing e composer rico. A Onda G (161–170) fechou paridade importante de
agent loop/contexto/plano/menções, e a Onda H (171–178) já está approved
para archive, blueprint, hooks, safety tiers, health, prompt-cache,
mobile e mailbox.

O próximo salto não é mais uma feature isolada: é a **camada de produto
cloud** que aparece no OpenWebUiCode/OpenHands — shell de navegação,
settings/profiles, catálogo de agentes, workflows/factories, control
plane de runtime, analytics/budgets, colaboração, extensions hub, chat UX
avançada, RAG e lifecycle enterprise.

## 2. OpenWebUiCode — o que migrar/adaptar

### 2.1 Layout e produto

Arquivos relevantes: `Layout/MainLayout.razor`, páginas `Workspace`,
`Ide`, `ChannelPage`, `Notes`, `Calendar`, `Automations`, `Customize`,
`Admin`, componentes `Settings/*` e specs arquivadas em `docs/specs/`.

Achados:

- Settings já possui redesign documentado em
  `SPEC-20261017-settings-ux-redesign`: categorias, save bar sticky,
  mobile chips, a11y e cobertura de Agents/Combos/Automations/External
  agents/Personas/Integrations/Skills.
- Há páginas de colaboração e produtividade que o Sandcastle não tem:
  channels, notes, calendar, generate/playground/watch e workspace item
  editor.
- `settings-ux-redesign`, `chat-ui-polish`, `chat-activity-feed`,
  `chat-branching`, `chat-detached-runs`, `pr-ci-panel` e
  `attachments-v2` mostram uma camada de conversa mais próxima do Devin
  Web que a conversa atual do Sandcastle.

### 2.2 Agentes, prompts e skills internas

Arquivos relevantes: `Infrastructure/agents/*.agent.md`,
`Infrastructure/SystemPrompt`, `Infrastructure/prompts/`,
`ChatTools/Tools/*.cs`.

Achados:

- Existem definitions declarativas (`code-reviewer`, `explore`,
  `general`, `researcher`) com precedência e catálogo.
- Há prompts internos especializados (`advisor-system.md`,
  `smart-permission-judge.md`) além do system prompt principal.
- O catálogo local tem 66 tools contra 55 schemas built-in encontrados
  no Sandcastle. Gaps de alto valor: `agent_validate`,
  `instant_context`, `memory_learn`, `reminder`, `code_interpreter`,
  `plan_exit`, `mode_switch`, `unseen_messages`, browser granular,
  `context_compact/recovery`, `extend_context` e media generation.
- Nem todo gap deve ser copiado: SPECs Sandcastle 174/176 já cobrem
  safety tiers, paralelismo, prompt cache, rewind, snapshot e usage. A
  nova SPEC-182 deve explicitamente integrar em vez de duplicar.

### 2.3 Conhecimento/RAG

Arquivos relevantes: specs `rag-knowledge`, `knowledge-v2`,
`retrieval-advanced`, `memory-pipeline`, endpoints `Knowledge`,
`Retrieval`, `Reference`.

Achados:

- O modelo upstream separa knowledge collections/documentos/chunks do
  memory pipeline. Sandcastle tem memory + suggestions + busca híbrida
  de código, mas ainda não tem uma plataforma RAG completa para documentos
  arbitrários com citations e collections por user/org/repo.

## 3. OpenHands OSS/frontend — o que adaptar

Arquivos relevantes: `src/routes/root-layout.tsx`, `settings.tsx`,
`settings-navigation.tsx`, `agent-profiles-settings.tsx`,
`llm-settings.tsx`, `mcp.tsx`, `extensions-hub.tsx`,
`factories-list.tsx`, `automation-templates.tsx`,
`automation-events.tsx`, `usage-tab.tsx`, `conversation.tsx`,
`planner-tab.tsx`, `browser-tab.tsx`, `commits-tab.tsx`.

Achados:

- O frontend já organiza o produto como um shell de cloud agent:
  conversas, projects, automations, factories, settings extensivo e
  extensions hub.
- Settings tem LLM profiles, provider connections, agent profiles,
  condenser, runtime, verification, secrets, MCP e experimental
  features — muito além do settings atual do Sandcastle.
- Factories agrupam automations e dependem do automation service; há
  templates e event center. Isso complementa routines/schedules já
  existentes no Sandcastle.
- `ExtensionsHub` é apenas entrada: no desktop redireciona para MCP, no
  mobile mostra hub consolidado. O Sandcastle precisa de um hub real que
  una MCP, plugins, skills, SCM/chat/tickets e canvas extensions.

## 4. OpenHands-Cloud — o que implementar como conceito próprio

Arquivos relevantes: `README.md`, `docs/ARCHITECTURE.md`,
`charts/openhands/values.yaml` e templates em
`charts/openhands/templates/`.

Arquitetura upstream conceitual:

- UI/conversations API + webhooks.
- Runtime API gerenciando warm runtimes e atribuição a conversas.
- Runtimes isolados executando agentes.
- Image loader garantindo cache de imagem nos nodes.
- PostgreSQL, Redis/Valkey, Keycloak, LiteLLM, object storage.

Features operacionais observadas nos templates/values:

- `budget-preflight`, `budget-maintenance`, `budget-reconcile-gate`.
- `proactive-convo-clean` e `app-conversation-start-task-clean`.
- `integration-events` e `mcp-events` como pipelines dedicados.
- `laminar`/ClickHouse para observabilidade e retenção de logs.
- `user-waitlist`, `enrich-user-interaction`, `resend-sync`.
- plugin directory, integrations hub, automation service e MCP ingress.

Restrição: o repositório é Polyform. As SPECs devem implementar conceitos
equivalentes em .NET e charts/docs próprios, nunca copiar YAML/código.

## 5. Matriz de gaps → nova leva de SPECs

| SPEC | Tema | Motivo principal |
|---|---|---|
| 179 | Shell de navegação estilo Devin + command center | Sandcastle tem páginas, mas ainda não tem shell/cloud command center coeso. |
| 180 | Settings center v2 + LLM/agent profiles | OpenHands/OpenWebUiCode tratam profiles/connections como centro de controle. |
| 181 | Agent definitions, prompt packs e internal skills runtime | OpenWebUiCode tem `*.agent.md`, advisor e permission judge; Sandcastle ainda não tem runtime declarativo equivalente. |
| 182 | Paridade de tools do agente v2 | 66 tools upstream versus 55 built-ins locais; faltam validation/context/browser/memory/reminder/media. |
| 183 | Workflows, factories e autonomous delivery | OpenHands tem factories/templates/events; faltam workflows nomeados e delivery loop opt-in. |
| 184 | Control plane de runtimes cloud | OpenHands-Cloud tem Runtime API/warm runtimes/image loader; Sandcastle precisa de fleet/capacity/lifecycle admin. |
| 185 | Observabilidade cloud, analytics e budgets | Budgets, tracing e analytics admin são centrais no produto cloud. |
| 186 | Workspace colaborativo | Channels, notes, calendar e notification feed ausentes. |
| 187 | Extensions/plugins/integrations hub | MCP/skills/marketplace existem, mas sem hub unificado e plugin ecosystem completo. |
| 188 | Chat UX cloud parity v2 | Branching, detached runs, PR/CI panel e feed rico aproximam do Devin Web. |
| 189 | Knowledge/RAG/context platform v2 | Memory/hybrid search existem, mas collections/RAG/citations não. |
| 190 | Enterprise cloud lifecycle operations | Waitlist/provisioning/cleanup/outbox/dead-letter/replay são a camada operacional SaaS ausente. |

## 6. O que já está coberto e não deve duplicar

- SPEC-171: archive/unarchive/delete v2.
- SPEC-172: blueprint v2.
- SPEC-173: hooks descobertos no repositório.
- SPEC-174: tool safety tiers/arg repair/paralelismo.
- SPEC-175: sandbox-agent health/watchdog.
- SPEC-176: prompt cache/context rewind/snapshot/usage.
- SPEC-177: mobile workspace UX.
- SPEC-178: team mailbox/delegate v2.

As novas SPECs referenciam essas dependências, mas não reabrem escopo já
approved — exceto quando explicitamente marcado como integração.

## 7. Recomendação de sequenciamento

1. **179 + 180** primeiro: shell e settings definem onde as demais
   superfícies vivem.
2. **181 + 182** em seguida: melhoram diretamente o agente e preparam
   workflows/autonomous delivery.
3. **183 + 188**: valor de produto imediato para usuários.
4. **184 + 185 + 190**: camada cloud/enterprise, com maior impacto de
   arquitetura e operação.
5. **186 + 187 + 189** conforme prioridade de colaboração/ecossistema;
   podem ser paralelizadas após shell/settings.
