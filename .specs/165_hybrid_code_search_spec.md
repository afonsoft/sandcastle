# SPEC — 165 · Busca de código híbrida (semântica + lexical)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `hybrid-code-search` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #492 · Issue #497 |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário do Ask/codebase indexing, **I want** busca que combine
lexical e semântica **so that** perguntas em linguagem natural encontram
código relevante mesmo sem match exato de termos — paridade com o
`code_search` híbrido do OpenWebUiCode (`083b216`).

## 2. Contexto / escopo

`EfCodeSearchStore.SearchAsync` usa `Contains` ingênuo por termos
(`Indexing/EfCodeSearchStore.cs`); `AskModeService` consome
`ICodeSearchStore.SearchAsync`. `LlmRole.Embedding` já existe no routing
(SPEC-149) mas **não há provider** (`IAgentLlmProvider` só tem chat).

Faltam:

- `IEmbeddingProvider` (OpenAI-compat `POST /v1/embeddings`) resolvido via
  `ModelRouter` role `Embedding`; config `Embeddings:Model` (vazio =
  desligado → degrade lexical).
- Armazenamento de embeddings por `CodeUnit` (tabela/coluna + migration
  dual) com reindex incremental.
- Fusão RRF (k=60) lexical+semântica no `SearchAsync`; degrade honesto
  (`degraded:true`) sem provider ou em falha de embeddings.
- Resultado navegável `{path,start_line,end_line,excerpt}` pronto para
  `file_read`.

Fora de escopo: embeddings de conversas/memória, rerank por LLM, indexação
de embeddings em Postgres pgvector (SQLite default; Postgres enterprise
pode usar coluna `vector` em follow-up).

## 3. Requisitos

- RF-001: `IEmbeddingProvider` em Application + implementação
  OpenAI-compat em Infrastructure (mesmo gateway/credenciais do chat,
  role `Embedding`); batch com budget de embeddings por call
  (`Search:MaxEmbeddingsPerCall`, default 512).
- RF-002: `CodeUnitRecord` ganha `EmbeddingJson` (float[] serializado) +
  migration dual; indexação preenche embeddings quando provider disponível
  (falha → unit sem embedding, nunca derruba index).
- RF-003: `SearchAsync` — quando há embeddings (query + units), funde
  rankings lexical e semântico via RRF (k=60); sem provider/embedding →
  lexical puro com `Degraded=true`; retorno inclui `degraded` e excerpt.
- RF-004: reindex incremental respeita units com embedding (só re-embeda
  units novas/alteradas por hash).
- RF-005: testes — unit RRF/degrade/budget, integração com embedding
  provider fake, migration dual.

## 4. API Contract

- Sem endpoints novos. `SearchCodeResult` ganha `degraded` (bool) e
  `excerpt` (string?). Ask response pode expor `search_degraded`.

## 5. Critérios de aceite

- **Dado** provider de embeddings configurado e units indexadas, **quando**
  busco por termo semântico ("autenticação de usuário"), **então** units
  relevantes aparecem no top-k mesmo sem match lexical exato.
- **Dado** provider ausente/falhando, **quando** busco, **então** resultado
  lexical com `degraded:true` e sem exceção.

## 6. Task Plan

1. `IEmbeddingProvider` + OpenAI-compat + role `Embedding` wiring.
2. Migration + indexação com embeddings.
3. RRF no `EfCodeSearchStore` + DTOs.
4. Testes unit/integração.

## 7. Deliverables

- Busca híbrida + provider de embeddings + migration + testes

## 8. Referências

- OpenWebUiCode `083b216` (`WorkspaceIndexService`, RRF k=60, budget 512)
- Sandcastle `src/Sandcastle.Infrastructure/Indexing/`

## 9. Guardrails

- Nunca bloquear busca por falha de embedding — degrade sempre.
- Budget de embeddings por call é obrigatório (custo).
- Embeddings nunca persistem conteúdo além do necessário (só vetores).

## 10. DoD

- [ ] RRF implementado com degrade; provider fake testado.
- [ ] Migration dual; reindex incremental; docs de config.
