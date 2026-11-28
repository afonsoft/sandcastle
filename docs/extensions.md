# Extensions and integrations hub

## English

SPEC-187 turns `/extensions` into a unified hub for MCP servers, marketplace entries, plugins and integrations. Plugins use a versioned manifest with declared permissions and support install, enable/disable and rollback metadata. The hub surfaces SCM and ticket integration counts, deep links to the existing configuration pages, and actionable provider test results for GitHub, GitLab, Slack, Linear, Jira, n8n and webhooks.

MCP OAuth, capability discovery, marketplace install flows, SCM connection tests, ticket integrations and secret redaction remain implemented in their existing subsystems; this hub aggregates them without duplicating their UX. New API surface: `GET /api/v1/extensions/summary`, plugin CRUD under `/api/v1/plugins`, and `POST /api/v1/integrations/{provider}/test`.

## Português

A SPEC-187 transforma `/extensions` em um hub unificado de servidores MCP, marketplace, plugins e integrações. Plugins usam manifesto versionado com permissões declaradas e suportam instalação, ativação/desativação e metadados de rollback. O hub mostra contagens de integrações SCM/tickets, deep links para as páginas de configuração existentes e resultados acionáveis de teste para GitHub, GitLab, Slack, Linear, Jira, n8n e webhooks.

OAuth MCP, discovery de capabilities, fluxos de marketplace, testes de conexão SCM, integrações de tickets e redaction de secrets continuam implementados nos subsistemas existentes; o hub os agrega sem duplicar UX. Nova superfície de API: `GET /api/v1/extensions/summary`, CRUD de plugins em `/api/v1/plugins` e `POST /api/v1/integrations/{provider}/test`.
