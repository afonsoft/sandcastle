# Autonomous-agent parity deep dive — 2026-11-28

## Scope and evidence boundary

Comparison requested by the user across layout, tools, system prompts, autonomous operation and supporting product surfaces. Sources used:

- OpenWebUiCode local fork, `origin/main` at `189bbc2` (plus local checkout `0d562a2`).
- OpenHands local fork, `origin/main` at `38efef1b1`.
- OpenHands docs: product architecture, runtime, Agent Server, Cloud UI, GitHub integration and skills.
- Cursor official docs: Agent overview, Plan Mode, Projects, Cloud Agents and MCP.
- Devin official docs: Ask/Agent modes, session tools, IDE/Browser/Shell progress surfaces.
- Claude Code official docs: agentic loop, tools, hooks, skills, memory and subagents.

Cursor, Devin and Claude Code system prompts are proprietary; this report compares documented behavior and observable product surfaces, not leaked or reverse-engineered prompts. OpenHands-Cloud/enterprise remains license-restricted; no code or assets are copied.

## Comparative matrix

| Area | External evidence | Sandcastle today | Gap |
|---|---|---|---|
| Agent loop | Cursor: instructions + tools + model, tuned per model; Claude: gather context → act → verify; Devin: Ask then Agent | ReAct/tool loop, advisor, compaction, task tracking, goal/wait/memory_reflect | Model-specific prompt adaptation is still coarse |
| Tools | Cursor: files, search, web, shell, browser, image, checkpoints, questions; OpenHands: tool catalog with selectable profiles; Devin: Shell/IDE/Browser observability | Broad built-in catalog already covers files, shell, web, browser, LSP, jobs, worktrees, documents, GitHub PRs, skills, tasks | Tool catalog exists internally, but user-facing per-profile selection/presentation is thinner |
| System prompt | OpenWebUiCode uses ordered contributors plus dynamic cache tier, skill catalog, autoinvoke skills, triggered knowledge notes and learned instincts | Ordered contributors exist; skills are injected when matched; memory/repo/blueprint/delegation blocks exist | No explicit cache tier split, progressive name/description skill catalog, path-triggered skills, or learned-instinct injection |
| Progressive disclosure | OpenHands skills advertise name/description first and support keyword/path triggers | Skills support Always/Keyword/Task and full-content injection | Missing Path trigger and catalog-only advertisement for non-injected skills |
| Planning | Cursor Plan Mode produces reviewable editable plans before build; Cursor Projects coordinate long-lived multi-agent work | SPEC-169 plan artifacts and plan mode exist | No persistent project coordinator/workspace concept |
| Cloud agents | Cursor Cloud Agents run isolated VMs, multiplayer steering, schedules/events, videos/screenshots/logs as evidence; OpenHands Cloud adds GitHub/Slack/secret/budget settings | Sandcastle has sandbox runtime, automations, factories, runtime control plane, budgets, collaboration | Evidence capture and unified operator progress surface remain weaker |
| Projects/multi-agent | Cursor Projects: coordinator plans and delegates, shared persistent project files/context; OpenHands has projects routes/coordinator conversation | Subagents, task tools, channels, factories and multi-repo support exist | No first-class Project aggregate linking coordinator, workers, board, files and memory |
| Layout/UX | Devin emphasizes unified Progress tab with shell/IDE/browser details; OpenHands routes include projects, planner, files, browser, usage, commits | Conversation has planner, files, browser, desktop, usage, commits, diff, terminal and activity cards | Shell command history/output preview/time navigation is not a single unified progress surface |
| Repository customization | OpenHands uses AGENTS.md plus SKILL.md progressive disclosure and path-triggered rules | `.agents`/repo context and skills exist | Path-triggered skill injection missing |
| Cloud product settings | OpenHands Cloud settings include integrations, app preferences, LLM, billing, secrets, API keys, MCP | Settings/control center, profiles, secrets, MCP, budgets, runtime admin exist | Strong coverage; remaining gaps are project/evidence polish rather than basic settings |

## Priority gaps selected for implementation

1. **Prompt cache + progressive skill context** — direct cost/latency and prompt-quality win; small enough for a focused implementation.
2. **Project coordinator workspaces** — largest product gap versus Cursor Projects and current OpenHands projects.
3. **Run evidence + unified progress UX** — visible trust/operator-control gap versus Devin and Cursor Cloud Agents.
4. **Model-aware instructions** — alignment with Cursor's model-specific tuning while preserving Sandcastle routing/profiles.
5. **Visual/docs refresh** — run the app, capture current screenshots and update README/auxiliary docs after implementation.

## Non-goals

- Do not copy proprietary prompts, code, artwork, screenshots or assets.
- Do not claim exact parity with proprietary internals; use documented behavior only.
- Do not weaken Sandcastle's existing security, secret-redaction or sandbox boundaries.
