# SPEC — 158 · Expansão de Settings: conta, API keys, notificações, manutenção

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `account-settings` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | #TBD · Epic #TBD |
| Scope | `src/` + `tests/` |

## 1. User Story

**As a** usuário, **I want** gerenciar minha conta — perfil, senha,
personal access tokens, webhook de notificações e preferências — num
lugar só **so that** não dependa de admin para operações de self-service
— paridade com a `SettingsModal` do OpenWebUiCode (conta, API key,
webhook, notificações, manutenção).

## 2. Contexto / escopo

`SettingsContent` cobre aparência, LLM/BYOK, secrets, usage, indexing,
skills. Faltam, por ordem de valor:

- **Tab Conta**: perfil (nome, avatar URL), troca de senha (provider
  `local`; desabilitada com hint quando OIDC/none), sessões ativas
  (lista + revogar).
- **Personal access tokens**: `POST /users/me/tokens {name, scopes[],
  expires_in_days?}` → `sc_pat_*` exibido 1× (SHA-256 armazenado, como
  secrets); `GET /users/me/tokens` (lista mascarada, último uso) +
  `DELETE`. Aceitos no `Authorization: Bearer` onde cookie vale —
  habilita CLI/CI/integrações sem sessão de browser.
- **Notificações**: toggles (run finished/failed, mention, resumo
  diário) + `notification_webhook_url` (HTTPS) + botão "testar" que
  dispara payload de exemplo. Browser push fica para SPEC futura.
- **Manutenção**: "exportar meus dados" (JSON dos itens do usuário) e
  "limpar caches locais" (localStorage+IndexedDB client-side).

Fora de escopo: web push/VAPID, SSO self-service, deleção de conta
(admin), avatar upload (só URL).

## 3. Requisitos

- RF-001: entidade `PersonalAccessToken {id, user_id, name,
  token_hash, scopes[], created_at, last_used_at?, expires_at?,
  revoked_at?}` + migration ×2; auth middleware aceita
  `Bearer sc_pat_*` → resolve usuário + scopes + atualiza
  `last_used_at` (throttle 60s); audit `pat.used`/`pat.created`/`pat.revoked`.
- RF-002: tab `Conta` — perfil edit (PATCH `/users/me`), senha
  (POST `/users/me/password` com `current_password`; 422 mismatch;
  desabilitada quando `Auth:Provider != local`), sessões (GET +
  DELETE `/users/me/sessions`).
- RF-003: tab `Tokens` — criar (modal com nome+expiração+scopes
  `read|write`), listar mascarado `sc_pat_…abcd`, revogar; token cru só
  no dialog de criação com copy-button.
- RF-004: tab `Notificações` — toggles persistidos (`UserSettings`
  jsonb) + webhook URL + `POST /users/me/notifications/test`; dispatcher
  chama webhook em run finished/failed (fire-and-forget, timeout 5s,
  retry 1×).
- RF-005: tab `Manutenção` — `GET /users/me/export` (json download) +
  botão "limpar caches" (client-side, confirmação).
- RF-006: i18n ×8 + testes (unit token hash/scope, integração endpoints,
  bUnit tabs).

## 4. API Contract

- `POST /api/v1/users/me/tokens {name,scopes[],expires_in_days?}` →
  `201 {id, token:"sc_pat_..."}` (única vez).
- `GET /api/v1/users/me/tokens` → `[{id,name,scopes,created_at,
  last_used_at,expires_at}]` (sem token).
- `DELETE /api/v1/users/me/tokens/{id}` → 204.
- `PATCH /api/v1/users/me {display_name,avatar_url?}`,
  `POST /users/me/password {current_password,new_password}`,
  `GET/DELETE /users/me/sessions`,
  `PUT /users/me/notification-prefs`, `POST /users/me/notifications/test`,
  `GET /users/me/export`.
- Auth PAT: `Authorization: Bearer sc_pat_*` + escopo `read` bloqueia
  métodos mutantes (403 `insufficient_scope`).

## 5. Critérios de aceite

- **Dado** token criado, **quando** `curl -H "Authorization: Bearer
  sc_pat_…" /api/v1/app-conversations`, **então** 200 autenticado; com
  escopo `read`, POST → 403.
- **Dado** webhook configurado e run que termina, **então** o URL
  recebe POST `{event:"run_finished",conversation_id,status}` em <10s.
- **Dado** troca de senha com senha atual errada, **então** 422 e a
  sessão continua válida.
- **Dado** export, **então** o JSON contém conversas/títulos/settings
  do usuário e nenhum secret em claro.

## 6. Task Plan

1. `PersonalAccessToken` + migration + middleware Bearer + testes.
2. Endpoints conta (perfil/senha/sessões) + testes.
3. Notificações: prefs + webhook dispatcher + teste (http mock).
4. Tabs Conta/Tokens/Notificações/Manutenção no `SettingsContent` +
   i18n ×8 + bUnit.
5. Export endpoint + docs + golden OpenAPI.

## 7. Deliverables

- PAT auth + endpoints de conta/notificações/export + 4 tabs Settings +
  i18n ×8 + testes

## 8. Referências

- OpenWebUiCode `SettingsModal.razor` (conta/api/notifications/maintenance)
- Sandcastle `AuthEndpoints`, `SettingsContent.razor`, secrets hashing

## 9. Guardrails

- Token armazenado só como SHA-256; nunca logar nem serializar cru.
- Senha: min 12 chars, argon2/pbkdf2 existente do `local` provider.
- Webhook: HTTPS only, SSRF-guard (mesma allowlist dos webhooks admin),
  sem retry em loop.
- Rotas novas no golden OpenAPI; scopes documentados.

## 10. DoD

- [ ] PAT autentica `curl` com escopo respeitado; revogação imediata.
- [ ] Webhook dispara em fim de run; teste manual via botão.
- [ ] Senha/sessões/perfil funcionais; export completo e sem secrets.
- [ ] i18n ×8, format/build/test verdes.
