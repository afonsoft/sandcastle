# Settings control center (SPEC-180)

Settings agora funciona como um centro de controle orientado a perfis,
inspirado na UX de settings do OpenWebUI e em produtos cloud de agentes.

## Shell de settings

`/settings` agrupa workspace, operations e account na rail lateral. Em
mobile, as mesmas seções viram chips horizontais. O formulário geral
acompanha estado dirty/saving/saved no formulário de agent/LLM e a aba **Sections** liga as superfícies
operacionais unificadas: MCP, extensions, skills, automations, secrets,
runtime/outposts, verification/scans e áreas admin-only de experimental/SCM.

## Provider connections

`Settings → Profiles` gerencia conexões tipadas para OpenAI, Anthropic,
Google, endpoints OpenAI-compatible e Ollama. As conexões usam endpoints
oficiais por padrão, podem ser testadas e listam modelos nativos do provider.
Valores secretos brutos nunca retornam pela API: segredos ficam no
`ISecretStore` e as respostas expõem apenas a referência do segredo.

Endpoints:

- `GET/POST/PUT/DELETE /api/v1/provider-connections`
- `POST /api/v1/provider-connections/{id}/test`
- `GET /api/v1/provider-connections/{id}/models`

## LLM profiles

LLM profiles nomeados combinam provider connection, modelo primário,
temperature, modelos por role e fallbacks ordenados. Podem ter escopo de
usuário ou org (escrita org exige owner/admin).

Em runtime, o profile selecionado sobrepõe o routing: modelos por role têm
precedência sobre o mapa pass-through legado, fallbacks viram um route plan
ordenado e temperature é enviada ao provider. Quando o profile referencia
uma provider connection, essa conexão fornece o endpoint do run e o
segredo armazenado quando o provider exige um.

Endpoints: `GET/POST/PUT/DELETE /api/v1/llm-profiles`.

## Agent profiles

Agent profiles nomeados combinam instruções de prompt pack, tools
permitidas, skills, agent type, defaults de mode, runtime profile e um LLM
profile opcional. Suportam create, update, duplicate e delete seguro. Deletes
retornam `409` enquanto conversas, automations ou profiles dependentes ainda
referenciarem o profile.

Quando selecionado na criação de conversa/automation, o Sandcastle
materializa defaults de mode/runtime/agent type/model e converte tools
permitidas em permission rules da conversa. Instruções do prompt pack e o
subconjunto de skills do profile são aplicados quando o system prompt é
reconstruído.

Endpoints: `GET/POST/PUT/DELETE /api/v1/agent-profiles` e
`POST /api/v1/agent-profiles/{id}/duplicate`.

## Seleção em conversas e automations

`POST /api/v1/app-conversations` e definições de automation aceitam
`llm_profile_id` e `agent_profile_id`; os formulários de nova conversa e
nova automation expõem os mesmos seletores. Profiles invisíveis ou inexistentes
são rejeitados com `400`; visibilidade user/org é aplicada no servidor.
Conversas antigas sem IDs de profile mantêm o comportamento legado.

Mudanças de schema têm migrations duais SQLite/PostgreSQL.
