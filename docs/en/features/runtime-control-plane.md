# Runtime control plane

`Admin → runtime` shows fleet, capacity, warm pools, images and operational alerts. Drain/terminate/recycle require super admin and are audited. Warm pool resize is process-local and applied by the warm-pool filler until restart.

Endpoints: `GET /api/v1/admin/runtime/fleet`, `/pools`, `/images`; `POST /drain`, `/terminate`, `/recycle`, `/resize`. It works with the local Docker engine and uses the same abstraction when runtime-api/k8s is configured.
