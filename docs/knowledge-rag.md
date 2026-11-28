# Knowledge RAG platform

## English

SPEC-189 adds collection-based knowledge retrieval on top of the existing knowledge suggestion pipeline:

- `/knowledge` now manages knowledge collections, manual document ingestion, chunk counts and retrieval testing.
- `POST /api/v1/knowledge/collections` creates a user/org-scoped collection; documents are ingested under `/collections/{id}/documents`.
- `POST /api/v1/knowledge/query` performs lexical retrieval by default. When `IEmbeddingProvider` is configured, chunk embeddings are stored and query scoring becomes hybrid vector + lexical. Without embeddings it degrades to lexical-only retrieval.
- Query citations include document title, source URI, ordinal, excerpt and score. User/org memory records promoted by the existing suggestion pipeline are included in retrieval as `memory:*` citations. Secrets are redacted before chunking/storage.
- `include_web=true` optionally combines provider-backed web search results. Web results use the existing cached `WebSearch` provider configuration and preserve provenance in citation URLs.
- Composer knowledge mention search now includes knowledge collections in addition to memory records and approved suggestions.
- SQLite and PostgreSQL migrations create `knowledge_collections`, `knowledge_documents` and `knowledge_chunks`.

Operational note: the initial retriever scores accessible chunks in memory. It is intentionally simple and suitable for small/medium collections; large deployments should add database-native vector indexes in a follow-up SPEC.

## Português

A SPEC-189 adiciona retrieval de knowledge por coleções sobre o pipeline existente de sugestões:

- `/knowledge` agora gerencia coleções, ingestão manual de documentos, contagem de chunks e teste de retrieval.
- `POST /api/v1/knowledge/collections` cria coleções com escopo de user/org; documentos são ingeridos em `/collections/{id}/documents`.
- `POST /api/v1/knowledge/query` executa retrieval lexical por padrão. Quando `IEmbeddingProvider` está configurado, os chunks armazenam embeddings e o score vira híbrido vetorial + lexical. Sem embeddings há fallback lexical.
- As citações incluem título, URI de origem, ordinal, trecho e score. Memory records de user/org promovidos pelo pipeline existente de sugestões entram no retrieval como citações `memory:*`. Secrets são redigidos antes de chunking/armazenamento.
- `include_web=true` combina opcionalmente resultados de web search. Esses resultados usam a configuração cacheada do provider `WebSearch` e preservam provenance nas URLs das citações.
- A busca de mentions do composer agora inclui coleções de knowledge além de memory records e sugestões aprovadas.
- Migrations SQLite e PostgreSQL criam `knowledge_collections`, `knowledge_documents` e `knowledge_chunks`.

Nota operacional: o retriever inicial pontua chunks acessíveis em memória. É intencionalmente simples para coleções pequenas/médias; deployments grandes devem adicionar índices vetoriais nativos em uma SPEC futura.
