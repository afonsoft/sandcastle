# Agent definitions e prompt runtime (SPEC-181)

`Settings → Profiles` agora inclui o catálogo de agent definitions e
prompt packs versionados usados pelo runtime de agentes.

## Agent definitions

Definitions declarativas continuam usando o formato `*.agent.md` da
SPEC-95, agora com frontmatter ampliado: `name`, `description`, `tools`,
`model`, `model_profile`, `skills`, `mode` e `permission_preset`. Além
das camadas builtin/embedded, arquivo do usuário e workspace, definitions
persistidas em banco entram na precedência user/org:

`builtin/system < org < user < workspace`.

O endpoint `POST /api/v1/agent-definitions/{id}/validate` valida campos
obrigatórios, mode permitido e tools conhecidas. A tool `task` aceita
`agent_scope` explícito (`builtin`, `system`, `user`, `org` ou
`workspace`) para delegar a uma definition no escopo correto; nomes
desconhecidos retornam erro com o catálogo válido.

Endpoints: `GET/POST/PUT/DELETE /api/v1/agent-definitions` e
`POST /api/v1/agent-definitions/{id}/validate`.

## Prompt packs versionados

Prompt packs agrupam prompt base, harness rules e instruções de plan
mode, build mode, advisor e permission judge. Updates não reescrevem a
versão antiga: criam uma nova linha ativa com versão incremental. O diff
linha a linha fica disponível em `compare`, e rollback copia uma versão
anterior para uma nova versão auditada.

Endpoints: `GET/POST/PUT/DELETE /api/v1/prompt-packs`,
`GET /api/v1/prompt-packs/{packId}/versions`,
`GET /api/v1/prompt-packs/{packId}/compare?from=1&to=2` e
`POST /api/v1/prompt-packs/{packId}/rollback/{version}`.

## Runtime, personas e segurança

Agent profiles podem referenciar um pack persistido com
`pack:<nome-do-pack>`; ao reconstruir o system prompt, o Sandcastle injeta
as seções ativas do pack na conversa, preservando instruções específicas
por mode e por conversa. Valores legados continuam sendo tratados como
instruções diretas.

Instruções retornadas pela API passam por redaction de referências de
secrets (`{{secret:...}}`) e outros valores sensíveis. Carregamento/uso de
skills e operações CRUD/update/rollback são auditados; usuários sem
autorização recebem 401/403 sem dados de outras personas.

Mudanças de schema têm migrations duais SQLite/PostgreSQL e contrato
OpenAPI golden atualizado.
