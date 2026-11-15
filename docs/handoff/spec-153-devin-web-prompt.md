# Devin Web Prompt — Complete SPEC-153 Hybrid Runner / Outpost

Copy the block below into a new Devin Web session.

---

You are working in the repository `afonsoft/sandcastle` (GitHub: https://github.com/afonsoft/sandcastle). Sandcastle is an MIT-licensed .NET 10 port of the OpenHands ecosystem (Blazor WebAssembly + ASP.NET Core minimal APIs + SignalR + EF Core SQLite/Postgres + Docker.DotNet sandboxes).

## Context — what is already done

SPEC-153 (`hybrid-runner` — Devin "Outposts"-style self-hosted sandbox execution inside a customer's network) is approved at `.specs/153_hybrid_runner_spec.md`. Read it first — it is the source of truth. Tracked by GitHub issue #447 under Epic #444.

The foundation is already implemented on branch `feature/spec-153-hybrid-runner` (build is green — 0 warnings, 0 errors). **Start your work by checking out this branch** — do NOT re-create it or redo this work:

- `src/Sandcastle.Domain/Identity/Permission.cs` + `RolePermissions.cs`: `outposts.view` + `outposts.manage` permissions, wired into role defaults and permission-name parsing.
- `src/Sandcastle.Application/Outpost/` — all contracts:
  - `IOutpostService.cs` — `OutpostStatuses` (pending|online|draining|offline|revoked), `OutpostPolicies` (cloud|prefer-outpost|outpost-only), records `OutpostInfo`, `OutpostEnrollResult`, `OutpostRegisterResult`, `OutpostRouteDecision`, and `IOutpostService` (Enroll/Register/List/Get/Drain/Deregister/ResolveChannelKey).
  - `IOutpostChannel.cs` — `IOutpostTaskDispatcher.SendTaskAsync`, `IOutpostRouter.ResolveAsync`/`PickOnlineAsync`.
  - `OutpostFrames.cs` — frame protocol: types `hello|heartbeat|task_assign|task_event|kill|drain|ack`, ops `provision|exec|read_file|write_file|destroy|inspect|pause|unpause|start|stop`, `OutpostFrame` record.
  - `OutpostOptions.cs` — `Outpost:*` config section.
- `src/Sandcastle.Infrastructure/Outpost/` — implementations:
  - `OutpostRecord.cs` — EF entity + `IEntityTypeConfiguration` → table `outposts`, indexes on `ChannelKeyHash`, `EnrollTokenHash`, `(OrgId, Status)`, `CreatedAtTicks`. Hashes only — plaintext secrets never persisted.
  - `OutpostService.cs` (279 lines), `OutpostChannelRegistry.cs` (494 lines — channel lifecycle), `OutpostRouter.cs`, `OutpostCertIssuer.cs` (mTLS cert issuance), `OutpostStatusSweeper.cs` (heartbeat→offline sweeper).
- `src/Sandcastle.Infrastructure/Conversations/ConversationRecords.cs`: `ConversationRecord.OutpostId` (Guid?, route pin).
- `src/Sandcastle.Infrastructure/Persistence/AppDbContext.cs`: `DbSet<OutpostRecord> Outposts`.
- EF migrations scaffolded for BOTH providers: `Persistence/Migrations/20261115182936_spec153_outposts*.cs` (SQLite) and `Persistence/Migrations/Postgres/20261115183052_spec153_outposts*.cs` — adds `outposts` table + `conversations.outpost_id` column. Model snapshots updated.
- Issue #447 is `in_progress`; branch has 3 commits already pushed to origin.

## What is MISSING — your job

Implement the remaining slices of SPEC-153 (see Task Plan §6 and DoD §10 in the spec). Follow the repo's existing conventions — study how SPEC-151 (alerts) and SPEC-152 (eval) wired endpoints, DI, workers and tests; they are the closest templates.

1. **API endpoints** — create `src/Sandcastle.Api/Endpoints/OutpostEndpoints.cs` mapped under `/api/v1/outposts` following the style of `AlertEndpoints.cs`/`EvalEndpoints.cs` and register the map in `Program.cs`:
   - `POST /api/v1/outposts/enroll` — `{org, name, labels}` → one-time enroll token (`perm:outposts.manage`).
   - `GET /api/v1/outposts` and `GET /api/v1/outposts/{id}` — list/detail (`perm:outposts.view`, org-scoped).
   - `POST /api/v1/outposts/{id}/drain`, `POST /api/v1/outposts/{id}/deregister` (`perm:outposts.manage`).
   - `POST /api/v1/outposts/connect` — WebSocket upgrade endpoint where the outpost agent connects (outbound-only from the agent side). Authenticate via channel key + mTLS client cert fingerprint per `Outpost:RequireMtls` (default true in prod config).
   - `PATCH /api/v1/conversations/{id}` — accept `outpost_id` for route pinning (check existing conversation PATCH endpoint and extend it, do not create a duplicate route).
2. **DI wiring** — register `IOutpostService`→`OutpostService`, `IOutpostRouter`→`OutpostRouter`, `IOutpostChannel`/`IOutpostTaskDispatcher`→`OutpostChannelRegistry`, `OutpostStatusSweeper` as hosted service, `OutpostCertIssuer`, and `OutpostOptions` (`services.AddOptions<OutpostOptions>().BindConfiguration("Outpost")`), wherever the other Infrastructure services are registered (find the `AddInfrastructure`-style extension used by `Program.cs`).
3. **Sandbox routing** — implement `OutpostSandboxService` (SPEC §RF-002): an `ISandboxService` decorator that delegates `SandboxContract` ops to the outpost over the registered channel when `OutpostRouter.ResolveAsync` picks an outpost; falls back to the local Docker `SandboxService` for `cloud`/`prefer-outpost` without online outpost; FAILS (no silent cloud fallback) when policy is `outpost-only` and no outpost is online. Wire it into wherever `ISandboxService` is consumed for conversation sandboxes (likely the conversation-start worker) respecting `ConversationRecord.OutpostId` pin → org policy → default.
4. **New binary `src/Sandcastle.Outpost/`** — .NET 10 worker console project added to `Sandcastle.slnx`: outbound-only WebSocket client to `/api/v1/outposts/connect`; `enroll` command (POST enroll → receives client cert + channel key, stores under `~/.sandcastle-outpost/` with 0600); heartbeat loop reporting `max_sandboxes` + labels; frame executor handling the `OutpostTaskOps` via local Docker.DotNet against the `sandcastle-runtime` image; `doctor` command (docker socket, egress reachability, cert validity, runtime image present); graceful `drain`; reconnect with exponential backoff; local secrets resolution (`OUTPOST_SECRETS_FILE` env file — secret VALUES never cross the channel, only names). Plaintext secrets shown once at enroll, never logged, never in argv.
5. **Admin UI** — Settings > Outposts page in `src/Sandcastle.Client` (follow the Alerts/Eval settings pages): enroll flow showing the generated `docker run sandcastle-outpost --enroll-token …` command once, list with status/labels/last-heartbeat, drain + deregister buttons, and conversation-level outpost picker. Client-side API contracts + i18n keys in ALL 8 locale files (the repo enforces `tools/check-i18n-parity.py` + `check-i18n-placeholders.py`).
6. **Audit** — `outpost_audit` entries (or reuse existing audit infra — check how other features audit) for connect/disconnect/task-assign/drain/deregister events.
7. **Tests** — add coverage following existing test-project conventions:
   - Unit: enroll token issuance/expiry, channel-key auth, register/hello, heartbeat transitions, drain/revoke behavior, router decisions (pin > org policy > default), `outpost-only` failure path, org isolation, permission enforcement, frame serialization round-trip, outpost agent frame executor (mock Docker).
   - Integration: enroll→connect→pinned conversation executes an exec on a local-docker outpost (Testcontainers/docker-available pattern used by existing integration tests), revoked outpost cannot reconnect.
   - Golden: update OpenAPI golden (`SANDCASTLE_UPDATE_GOLDEN=1`) and `tools/check-mcp-tools-golden.py` baseline ONLY if MCP tools changed.
8. **Config/docs** — `Outpost` section defaults in `appsettings.json` (`RequireMtls`, `LogRedaction=aggressive`, `HeartbeatTimeoutSeconds`, `DefaultPolicy`), README/`docs/` note on running an outpost.

## Security requirements (hard rules — from SPEC §RF-003/§5/§9)

- Outbound-only: the outpost binary opens NO inbound listener.
- Enroll tokens and channel keys stored as SHA-256 hashes only (record already does this — keep it); plaintext emitted once, never logged/argv'd.
- Secret values NEVER cross the channel — only `SecretNamesJson` names; values resolve on the outpost side.
- `kill` frames terminate tasks when an outpost is revoked; revoked outposts must not reconnect (status `revoked` hard-fails handshake).
- mTLS enforced when `Outpost:RequireMtls=true`; validate `CertFingerprint`.
- Org isolation: every query/path is org-scoped; tokens are org-scoped; one org's outpost cannot see another org's tasks.
- Do NOT break the existing `ISandboxService` — the outpost path is additive (decorator/router), never a replacement.

## Repo conventions you must follow

- `CLAUDE.md` at repo root is the agent guide — read it. Docs/comments in pt-BR; code/commits/APIs in English (snake_case JSON). Conventional Commits.
- `main` is protected — work on `feature/spec-153-hybrid-runner`, open ONE PR with `Closes #447`, merge with squash when green.
- EF changes require BOTH migrations (SQLite `AppDbContext` + `PostgresDbContext`, output dirs `Persistence/Migrations` and `Persistence/Migrations/Postgres`, startup project `src/Sandcastle.Api`):
  `dotnet ef migrations add <name> -p src/Sandcastle.Infrastructure -s src/Sandcastle.Api --context AppDbContext -o Persistence/Migrations` (and same with `--context PostgresDbContext -o Persistence/Migrations/Postgres`). Already done for the base tables — only re-scaffold if you change the model.
- Issue #447 labels: move `in_progress`→`in_review`→`in_pullrequest`→`done` as you progress (`gh issue edit 447 --remove-label X --add-label Y`).
- After merge: move `.specs/153_hybrid_runner_spec.md` → `docs/specs/` and update `.specs/README.md` + `docs/specs/README.md` indexes (archive commit, can be a small follow-up PR).
- Update `.agents/memory/20261115-memory.md` (append-only) and `.agents/memory/memory.md` (state, ≤100 lines) per the memory protocol.

## Verification (all must pass before claiming done)

```bash
dotnet format --verify-no-changes
dotnet build Sandcastle.slnx -c Release          # expect 0 warnings 0 errors
dotnet test                                     # full suite; run serially if flaky
python3 tools/check-css-classes.py
python3 tools/check-form-a11y.py
python3 tools/check-i18n-parity.py
python3 tools/check-i18n-placeholders.py
python3 tools/check-mcp-tools-golden.py
bash tools/check-spec-lifecycle.sh
```

Known environmental flakes (rerun isolated before treating as failures): SSE timing tests, filesystem tests, dev-login, ETag, sandbox-load tests. Note: `Spec149ModelRouterApiTests.cs` has a known `dotnet format` drift on main — fix it as a drive-by.

## Do NOT redo (already merged on main — pull/rebase if needed)

PR review agent (SPEC-140), CI autofix (141), QA evidence (142), code scans (143), batch refactor (144), repo wiki+MCP (145), multi-repo conversations (146), product polish (147), marketplace (148), model router (149), CLI handoff (150), alert integrations (151), benchmark adapters (152).

## Deferred — out of scope for this task (do not implement)

LLM execution on the outpost (air-gapped phase 2), outpost HA clusters, GPU scheduling, Windows outpost, k8s operator, `sandcastle ssh`, Security Swarm threat-model phase, SARIF import, public marketplace registry, stacked PRs outside GitHub, ask citation fan-out.
