# SPEC-20261103: Headless Runner & JsonLines Observer Integration

## Status
Approved

## Context
Portabilidade das melhorias recentes do ecossistema OpenWebUiCode para o Sandcastle (`HeadlessRunner` e `JsonLinesRunObserver`), permitindo execução headless nativa e observabilidade baseada em NDJSON (JSON Lines).

## Requirements
1. **Headless Execution**: Disponibilizar o `HeadlessRunner` na API/CLI para execuções sem GUI em ambientes de automação e CI.
2. **NDJSON Logging**: Utilizar o `JsonLinesRunObserver` para registrar eventos de agentes em formato stream-friendly.

## Acceptance Criteria
- Build 100% verde (0 erros).
- Módulos integrados na solução.
