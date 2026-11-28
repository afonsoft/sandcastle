# Agent prompt cache tiers and progressive skills

## English

SPEC-194 introduces two prompt-context optimizations:

- **Cache-tier composition**: system-prompt contributors declare `static` or
  `dynamic` cache tiers. Static sections are emitted before dynamic sections
  so providers can reuse the stable prefix. The `SystemPromptEvent` records
  bounded `prompt_cache` metadata (section names, order, tier and character
  counts) for diagnostics; it does not change provider tool or message wire
  contracts.
- **Progressive skill disclosure**: triggered or pinned skills are still
  injected in full. Other visible skills are advertised in a bounded catalog
  by name and description only. The agent can load one explicitly with the
  `invoke_skill` tool.
- **Path triggers**: skills may use a `path` trigger such as `src/web/**`,
  `docs/specs/*.md` or an exact workspace-relative path. It matches paths
  referenced in the user message or already touched by the run.

Existing `always`, `keyword` and `task` triggers, skill precedence and
SPEC-168 allowed-tools enforcement are unchanged.

## Português

A SPEC-194 introduz duas otimizações de contexto do prompt:

- **Composição por camada de cache**: contribuidores do system prompt
  declaram camada `static` ou `dynamic`. Seções estáticas são emitidas antes
  das dinâmicas para permitir reúso do prefixo estável pelo provedor. O
  `SystemPromptEvent` registra metadados limitados de `prompt_cache` (nome da
  seção, ordem, camada e tamanho) para diagnóstico, sem alterar o contrato de
  mensagens/tools enviado ao provedor.
- **Disclosure progressivo de skills**: skills acionadas ou fixadas continuam
  injetadas integralmente. Demais skills visíveis aparecem em um catálogo
  limitado apenas com nome e descrição. O agente pode carregar uma skill
  explicitamente com a tool `invoke_skill`.
- **Triggers por path**: skills podem usar trigger `path`, por exemplo
  `src/web/**`, `docs/specs/*.md` ou um caminho exato relativo ao workspace.
  O trigger considera caminhos citados na mensagem do usuário ou já tocados
  pela run.

Os triggers `always`, `keyword` e `task`, a precedência de skills e a
aplicação de `allowed_tools` da SPEC-168 permanecem inalterados.
