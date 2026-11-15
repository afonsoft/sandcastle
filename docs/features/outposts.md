# Outposts — execução self-hosted (SPEC-153)

Um **outpost** é um runner `sandcastle-outpost` que roda dentro da rede do
cliente e executa sandboxes no Docker local. Todo o tráfego é
**outbound-only**: o outpost abre um único WebSocket para o plane
(`POST /api/v1/outposts/connect`) — nenhuma porta inbound é aberta.

## Modelo de segurança

- Frames `outpost_frame_v1` assinados com HMAC-SHA256 usando a channel key
  `opk-*` (SHA-256 em repouso, plaintext exibido uma única vez no enroll).
- mTLS opcional: `Outpost:RequireMtls=true` valida o fingerprint do cert
  registrado no handshake.
- **Segredos nunca cruzam o canal**: `task_assign` carrega apenas NOMES;
  o outpost resolve valores do ambiente local (`SC_OUTPOST_SECRET_<NAME>`).
- `kill` encerra tasks em voo; outposts revogados não reconectam.
- Todas as queries são isoladas por org.

## Provisionamento

```bash
# no host do cliente:
docker run -d --name sandcastle-outpost \
  -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/afonsoft/sandcastle-outpost:latest \
  enroll https://plane.exemplo.com <enroll-token> --name edge-1

sandcastle-outpost doctor   # valida config + socket do docker
sandcastle-outpost run      # conecta, hello, heartbeat, loop de frames
```

`enroll` persiste `~/.sandcastle-outpost/config.json` (0600) e o par mTLS
quando emitido. Reconnect usa backoff exponencial até 1 min.

## Roteamento

Ordem de decisão do `IOutpostRouter`: pin da conversa
(`conversations.outpost_id`, via `PATCH /app-conversations/{id}`
`outpost_id`) → residência da org (`data_residency=internal-only`) →
política (`outpost_policy` | `Outpost:DefaultPolicy` = `cloud` |
`prefer-outpost` | `outpost-only`). `outpost-only` sem outpost online
**falha** (nunca cai para cloud silenciosamente).

## Configuração (`appsettings.json`)

```json
"Outpost": {
  "Enabled": true,
  "RequireMtls": null,
  "HeartbeatSec": 30,
  "OfflineAfterSec": 90,
  "EnrollTokenTtlHours": 24,
  "DefaultPolicy": "cloud",
  "TaskTimeoutSec": 300,
  "CertValidityDays": 365,
  "LogRedaction": "aggressive"
}
```

Endpoints REST em `/api/v1/outposts`: `POST /enroll` + `POST /{id}/drain`
+ `POST /{id}/deregister` (`perm:outposts.manage`), `GET /` +
`GET /{id}` (`perm:outposts.view`), `POST /register` (handshake anônimo),
`POST /connect` (WebSocket). Auditoria: `outpost.enroll|register|connect|
disconnect|task_assign|drain|deregister`.
