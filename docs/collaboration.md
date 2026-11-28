# Collaboration workspace

## English

SPEC-186 adds an org-scoped collaboration workspace:

- `/channels`: group/DM channels with messages, reactions, unread state, pins and links back to conversations.
- `/notes`: collaborative markdown notes with autosave-on-blur and monotonic versions.
- `/calendar`: consolidated view of automation schedules and workflow runs.
- `/notifications`: unified read/unread notification feed with deep links.

The API is versioned under `/api/v1`: `channels`, `notes`, `notifications` and `calendar/events`. Data is isolated by owner or organization membership and schema is available in both SQLite and PostgreSQL migrations.

## Português

A SPEC-186 adiciona um workspace colaborativo com escopo de org:

- `/channels`: canais de grupo/DM com mensagens, reactions, estado de não lidas, pins e links para conversas.
- `/notes`: notas markdown colaborativas com autosave ao sair do editor e versões monotônicas.
- `/calendar`: visão consolidada de agendas de automations e runs de workflows.
- `/notifications`: feed unificado de notificações lidas/não lidas com deep links.

A API versionada fica em `/api/v1`: `channels`, `notes`, `notifications` e `calendar/events`. Os dados são isolados por dono ou membership da organização e o schema possui migrations SQLite e PostgreSQL.
