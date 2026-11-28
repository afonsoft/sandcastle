# Paridade de tools do agente v2 (SPEC-182)

A SPEC-182 amplia o catálogo interno de tools do agente com operações de
validação, contexto, browser granular, mídia opcional e aliases de
compatibilidade, sem adicionar endpoints REST primários.

## Tools de execução e memória

- `agent_validate`: detecta o stack do workspace (`dotnet test`, `npm test`,
  `pytest`, `go test`, `cargo test`) ou executa `command` explícito; retorna
  exit code, timeout, saída redigida e evidência de `git status`.
- `code_interpreter`: executa snippets `python3`/`node` no sandbox com
  timeout limitado e saída separada em stdout/stderr.
- `instant_context`: coleta contexto somente leitura por comandos restritos
  (`rg`, `readfile`, `tree`) e devolve XML estrito com snippets.
- `memory_learn`: persiste lições duráveis por key no armazenamento project/org
  já existente.
- `reminder` + `unseen_messages`: reminders conversation-local ficam em
  memória persistente e são surfaciados quando vencem, junto com mensagens do
  usuário ainda não vistas.
- `plan_exit` e `mode_switch`: transições explícitas de modo. Saída para build
  respeita o artefato de plano versionado da SPEC-169.

## Browser granular

`browser_navigate`, `browser_observe`, `browser_act`, `browser_screenshot` e
`browser_close` expõem as ações do browser isolado por conversa sem quebrar a
tool monolítica `browser`. Screenshots retornam em `screenshot_base64` na
observação persistida no event stream, permitindo visual reasoning e auditoria.

## Context ops

`context_compact` chama compactação manual; `context_recovery` recupera o
summary mais recente; `context_usage` devolve o ledger operacional
(`tokens_estimate`, `window`, `compactions`, `model`, `updated_at`);
`extend_context` concede até duas extensões por conversa e o loop consome uma
concessão para adiar uma compactação de emergência.

## Mídia opcional

`generate_image` e `generate_video` só operam quando a feature está habilitada
e um provider OpenAI-compatible está configurado:

```text
Media:ImageGenerationEnabled=true
Media:ImageProvider=openai-compatible
Media:ImageEndpoint=https://provider.example/v1/images/generations
Media:ImageModel=<modelo>
Media:ImageApiKey=<secret por configuração segura>
```

Para vídeo, usar as chaves equivalentes `Media:Video*`. A resposta esperada é
`data[0].b64_json` ou `data[0].url`; bytes são gravados no workspace em
`/workspace/repo/generated/`. Sem flag/provider, ou quando o provider falha, a
tool retorna erro estruturado explícito — nunca falha silenciosamente.

## Aliases de paridade

`shell_exec` delega para `bash`, `fetch_url` para `web_fetch` e `todo_write`
para `task_tracker`. Eles são registrados como tools deferred: ficam
descobríveis por `tool_search`/catálogo completo sem duplicar o prompt inicial
com nomes redundantes.

## Segurança e testes

Tools de escrita/execução (`agent_validate`, `code_interpreter`, browser act,
media e aliases de shell) são `Risky` e passam pelo sistema de permissões.
A suíte cobre schema/registro/aliases, dispatch no browser, contexto,
reminders, erros explícitos de mídia e integração real em sandbox Docker para
`instant_context`/`agent_validate`.
