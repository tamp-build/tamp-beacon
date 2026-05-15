# Deployment reference

Two reference deploy shapes for `tamp-beacon`:

| Shape | Files | When |
|---|---|---|
| `docker compose` | [`docker-compose.yml`](./docker-compose.yml) | Single host, homelab, inner-loop verification |
| Kubernetes StatefulSet | [`kubernetes/*.yaml`](./kubernetes/) | Cluster deploy with cert-manager-provisioned TLS |

Both pull `ghcr.io/tamp-build/tamp-beacon:0.1.0` directly. See the [README's tag scheme table](../README.md#image-tag-scheme) for the immutable-vs-floating tag tradeoffs.

## docker compose

```bash
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.yml logs -f beacon   # watch for the setup token
```

Open `http://localhost:8080`, consume the token printed in the logs to mint the first sysadmin (see the [README quickstart](../README.md#quickstart--pull-and-run-the-published-image)), and you're in.

For HTTPS, front the container with your existing reverse proxy. Caddy is the lowest-friction option:

```caddy
beacon.example.com {
    reverse_proxy localhost:8080
}
```

## Kubernetes StatefulSet

```bash
# Adjust storageClassName, ingressClassName, host names in the manifests first.
kubectl apply -f deploy/kubernetes/

# Wait for the pod to come up and stream the setup token from its logs.
kubectl rollout status statefulset/tamp-beacon
kubectl logs -f statefulset/tamp-beacon | grep -A 4 'setup token'
```

Then `POST /setup` against your Ingress host with the token in hand:

```bash
curl -sS -X POST https://beacon.example.com/setup \
  -H 'content-type: application/json' \
  -d '{"token":"<token>","username":"admin","password":"<your-password>"}'
```

### External Postgres (optional)

The StatefulSet's commented-out `BEACON_DB_CONNECTION_STRING` env switches the beacon off bundled Postgres and onto an external instance. Bring your own `Secret`:

```bash
kubectl create secret generic tamp-beacon-secrets \
  --from-literal=db-connection-string='Host=postgres.example.com;Database=beacon;Username=beacon;Password=...'
```

Then uncomment the `valueFrom` block in `statefulset.yaml`. The PVC is still useful for the setup token + VAPID key persistence, so leave the `volumeClaimTemplates` in place even in external-Postgres mode.

### Web Push and HTTPS

Browsers refuse to register a Service-Worker push subscription over plain HTTP. The reference `ingress.yaml` assumes a TLS secret named `tamp-beacon-tls`; provision it however your cluster does — cert-manager + Let's Encrypt is the common path:

```yaml
metadata:
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-prod
```

### Sizing

Tamp's per-build telemetry is tiny — a few KB of span attrs per build at most. The default `resources` block (100m CPU / 256Mi memory request, 1 CPU / 1Gi limit) handles tens of builds per minute comfortably. The PVC default of 10Gi is enough for years of single-team usage; bump it if you're routing many busy CI projects through one beacon.

### Backups

The whole beacon state lives in the PVC — Postgres datadir + setup token + VAPID key. Snapshot it (whatever your CSI driver provides) for backup; that's the entire restore surface.

## Sanity check

After standing up either shape:

```bash
curl https://beacon.example.com/healthz       # → {"status":"ok"}
curl https://beacon.example.com/readyz        # → {"status":"ready","setup_complete":true,"awaiting_setup":false}
```

If `setup_complete` is `false`, the setup token still needs to be consumed.
