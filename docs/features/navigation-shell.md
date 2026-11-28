# Navigation shell (SPEC-179)

Shell de produto inspirada no Devin Web para conversas e operações cloud.

## Topbar contextual

No desktop, a topbar mostra breadcrumbs por área (`/conversations`,
`/automations`, `/workspace`, `/settings`, `/extensions`, `/factories`),
contadores operacionais e ações globais. Em mobile, a navegação continua
pela topbar compacta existente.

## Command center

`Ctrl+K` (ou `/`) abre a command palette. Além de conversas recentes e
ações rápidas, ela indexa as áreas de produto: automations, knowledge,
workspace, extensions, factories, MCP/skills e settings.

## Lista de sessões

A sidebar mantém status live/attention das conversas e exibe custo
agregado por sessão quando há `TokenEvent.cost_usd`. O painel da conversa
continua redimensionável por conversa.

## Focus mode

O botão de foco na topbar oculta a sidebar e amplia o painel principal;
a preferência persiste em `sessionStorage` (`sc.shell.focus`).

## Rotas foundation

- `/extensions` — entrada foundation do hub unificado de extensões;
  a implementação completa fica com a SPEC-187.
- `/factories` — entrada foundation de workflow factories/delivery;
  a implementação completa fica com a SPEC-183.

## Telemetria UX

`POST /api/v1/ux-events` recebe eventos de navegação (`route_navigation`)
com duração de renderização. O backend reduz a rota à área de produto
antes de logar — IDs, títulos e conteúdo de conversas nunca são gravados.
`GET /api/v1/navigation/summary` alimenta badges com contagens de
attention, running, automations, factories e extensions.
