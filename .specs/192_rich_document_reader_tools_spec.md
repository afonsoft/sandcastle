# SPEC — 192 · Rich document reader tools

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `rich-document-reader-tools` |
| Type | `Feature` |
| Stack | `.NET 10 · ASP.NET Core` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Source analysis | `docs/research/2026-11-openwebui-chat-main-comparison.md` |

## 1. Problem

OpenWebUiCode main adds structured readers for PDFs, archives and SQLite databases. Sandcastle only has generic text file reads, limiting agent analysis of documents and artifacts and weakening future RAG ingestion.

## 2. Requirements

- RF-001: `read_pdf` extracts bounded text with page markers, size/time limits and clear unsupported/encrypted errors.
- RF-002: `read_archive` lists and extracts bounded text entries from zip/tar-like archives without traversal outside the workspace.
- RF-003: `read_sqlite` lists tables/schema and executes read-only bounded SELECT queries with row/byte caps.
- RF-004: outputs include provenance metadata (path, page/entry/table, truncation state) suitable for citation and future SPEC-189 ingestion.
- RF-005: secrets redaction, binary-safe behavior, cancellation/timeouts and workspace path validation.
- RF-006: unit tests with generated fixtures plus integration/tool-registry coverage; optional dependencies documented.

## 3. Acceptance criteria

- Agent can answer questions about PDFs, archive contents and SQLite data using structured bounded output.
- No write-capable SQL or archive extraction outside workspace is possible.
