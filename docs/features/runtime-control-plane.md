# Control plane de runtimes

`Admin → runtime` mostra fleet, capacidade, warm pools, imagens e alertas operacionais. As operações de drain/terminate/recycle exigem super admin e são auditadas. Resize de warm pool é process-local e aplicado pelo warm-pool filler até restart.

Endpoints: `GET /api/v1/admin/runtime/fleet`, `/pools`, `/images`; `POST /drain`, `/terminate`, `/recycle`, `/resize`. Funciona com engine Docker local e usa a mesma abstração quando runtime-api/k8s estiver configurado.
