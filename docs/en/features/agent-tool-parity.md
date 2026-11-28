# Agent tool parity v2 (SPEC-182)

SPEC-182 extends the agent's internal tool catalog with validation, context,
granular browser, optional media and compatibility aliases without adding new
primary REST endpoints.

## Execution and memory tools

- `agent_validate`: detects the workspace stack (`dotnet test`, `npm test`,
  `pytest`, `go test`, `cargo test`) or runs an explicit `command`; it returns
  exit code, timeout, redacted output and `git status` evidence.
- `code_interpreter`: runs `python3`/`node` snippets inside the sandbox with a
  bounded timeout and separate stdout/stderr.
- `instant_context`: collects read-only workspace context through restricted
  commands (`rg`, `readfile`, `tree`) and returns strict XML snippets.
- `memory_learn`: persists durable lessons by key in the existing project/org
  memory store.
- `reminder` + `unseen_messages`: conversation-local reminders are persisted
  and surfaced when due, together with user messages not yet seen.
- `plan_exit` and `mode_switch`: explicit mode transitions. Entering build
  respects the versioned plan artifact state machine from SPEC-169.

## Granular browser

`browser_navigate`, `browser_observe`, `browser_act`, `browser_screenshot` and
`browser_close` expose the isolated per-conversation browser without breaking
the monolithic `browser` tool. Screenshots are returned as `screenshot_base64`
in observations persisted to the event stream for visual reasoning and audit.

## Context operations

`context_compact` invokes manual compaction; `context_recovery` returns the
latest durable summary; `context_usage` returns the operational ledger
(`tokens_estimate`, `window`, `compactions`, `model`, `updated_at`);
`extend_context` grants at most two extensions per conversation and the loop
consumes one grant to defer one emergency compaction.

## Optional media

`generate_image` and `generate_video` only operate when the feature flag and
an OpenAI-compatible provider are configured:

```text
Media:ImageGenerationEnabled=true
Media:ImageProvider=openai-compatible
Media:ImageEndpoint=https://provider.example/v1/images/generations
Media:ImageModel=<model>
Media:ImageApiKey=<secret through secure configuration>
```

Use the equivalent `Media:Video*` keys for video. The expected response is
`data[0].b64_json` or `data[0].url`; bytes are written to the workspace under
`/workspace/repo/generated/`. Without a flag/provider, or when the provider
fails, the tool returns an explicit structured error — it never fails silently.

## Parity aliases

`shell_exec` delegates to `bash`, `fetch_url` to `web_fetch`, and `todo_write`
to `task_tracker`. They are registered as deferred tools: discoverable through
`tool_search`/the full catalog without duplicating the initial prompt with
redundant names.

## Security and tests

Write/execution tools (`agent_validate`, `code_interpreter`, browser act,
media and shell aliases) are `Risky` and flow through the permission system.
Coverage includes schema/registration/aliases, browser dispatch, context,
reminders, explicit media errors and real Docker sandbox integration for
`instant_context`/`agent_validate`.
