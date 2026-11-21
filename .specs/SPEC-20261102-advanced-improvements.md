# SPEC-20261102: Advanced Improvements & Telemetry Parity

## Status
Approved

## Context
Comparação com o ecossistema de referência `afonsoft/OpenWebUiCode` evidenciou a oportunidade de incorporar telemetria granular de custos/tokens por chat, validação estrita de esquemas MCP e ampliação de testes de integração via Testcontainers para o ciclo de vida de sandbox e snapshots.

## Requirements
1. **Telemetry & Insights**: Expandir métricas de execução de run com tracking de tokens, latência de tool calls e consumo por sessão.
2. **MCP Validation**: Garantir parsing estrito de argumentos nas ferramentas MCP consumidas pelo agente.
3. **Integration Tests**: Adicionar suíte de integração cobrindo hot-restart e restauração de snapshots.

## Acceptance Criteria
- Build 100% verde (0 erros).
- Testes passando sem regressões.
