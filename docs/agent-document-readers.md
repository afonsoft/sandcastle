# Agent rich document readers

## English

SPEC-192 adds three read-only sandbox tools:

| Tool | Behavior |
| --- | --- |
| `read_pdf` | Extracts bounded PDF text with `[page N]` markers, SHA-256/size provenance, page/character/time limits, and explicit encrypted or unsupported errors. |
| `read_archive` | Lists or reads bounded entries from zip/tar archives in memory only; entries are never extracted to disk, so archive traversal cannot escape the workspace. |
| `read_sqlite` | Lists tables/schema or executes one read-only `SELECT`/`WITH` query through SQLite `mode=ro&immutable=1` with row and byte caps. |

Paths must stay inside the workspace. Outputs include provenance (`path`, size, SHA-256, extractor/engine) suitable for citation and future knowledge ingestion. Text is binary-safe and secret-redacted before it becomes tool output.

PDF extraction uses `pypdf`/`PyPDF2` when available, then `pdftotext`, then a limited stdlib fallback for simple uncompressed/deflated PDFs. For full PDF fidelity, install `pypdf` in the sandbox image; this is an optional runtime dependency, not a host NuGet dependency.

## Português

A SPEC-192 adiciona três tools read-only no sandbox:

| Tool | Comportamento |
| --- | --- |
| `read_pdf` | Extrai texto limitado de PDF com marcadores `[page N]`, provenance SHA-256/tamanho, limites de páginas/caracteres/tempo e erros explícitos para PDF criptografado/não suportado. |
| `read_archive` | Lista ou lê entradas limitadas de archives zip/tar apenas em memória; nada é extraído em disco, impedindo traversal fora do workspace. |
| `read_sqlite` | Lista tabelas/schema ou executa um `SELECT`/`WITH` read-only via `mode=ro&immutable=1` com limites de linhas e bytes. |

Paths precisam permanecer no workspace. Saídas incluem provenance (`path`, tamanho, SHA-256, extractor/engine) adequada para citação e ingestão futura de conhecimento. Texto é binary-safe e redigido antes de virar output da tool.

A extração de PDF usa `pypdf`/`PyPDF2` quando disponível, depois `pdftotext` e por fim um fallback stdlib limitado para PDFs simples. Para fidelidade completa, instale `pypdf` na imagem do sandbox; é uma dependência opcional de runtime, não uma dependência NuGet do host.
