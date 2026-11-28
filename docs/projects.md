# Project workspaces / Workspaces de projeto

## English

Projects coordinate multi-repository work. A project has repositories, an optional coordinator conversation, worker conversations, bounded shared memory entries, and bounded text files. Coordinators decompose and review work; workers implement bounded slices and report evidence without changing project scope or security constraints.

API endpoints live under `/api/v1/projects`. Project context is injected into coordinator and worker prompts, capped to avoid prompt bloat. Shared memory is limited to 100 entries of 8 KiB each; shared text files are limited to 50 files of 16 KiB each.

## Português

Projetos coordenam trabalho multi-repositório. Um projeto possui repositórios, uma conversa coordenadora opcional, conversas workers, memória compartilhada limitada e arquivos de texto limitados. Coordenadores decompõem e revisam trabalho; workers implementam fatias delimitadas e reportam evidências sem alterar escopo ou restrições de segurança.

Os endpoints ficam em `/api/v1/projects`. O contexto do projeto é injetado nos prompts de coordenador e workers, com limite para evitar excesso no prompt. A memória compartilhada permite 100 entradas de 8 KiB; arquivos compartilhados permitem 50 arquivos de texto de 16 KiB.
