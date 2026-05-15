# tamp-beacon

**A self-hosted, single-image OpenTelemetry receiver and dashboard for [Tamp](https://github.com/tamp-build/tamp) builds.**

Tamp builds emit a pinned diagnostics contract (ADR 0018) — three `ActivitySource`s and one `Meter`, with ~80 stable tag keys covering build / target / command spans plus host and CI facets. `tamp-beacon` receives exactly that contract and gives you a queryable history, a browser dashboard, and Web Push notifications on build failure. **One container, bundled Postgres, backed up by a single PVC snapshot.**

| | |
|---|---|
| Image | `ghcr.io/tamp-build/tamp-beacon:0.1.0` (also `:0.1`, `:latest`, and `:main` for trunk) |
| Platform | `linux/amd64` (arm64 deferred to v0.2) |
| Port | `8080` (OTLP/HTTP receiver + dashboard + admin UI on the same port) |
| Volume | `/var/lib/tamp-beacon` (Postgres datadir + setup token + VAPID key) |
| Status | v0.1.0 shipped 2026-05-15 — feature-complete receiver + dashboard |

## Quickstart — pull and run the published image

```bash
docker run -d --name tamp-beacon \
  -p 8080:8080 \
  -v tamp-beacon-data:/var/lib/tamp-beacon \
  ghcr.io/tamp-build/tamp-beacon:0.1.0
```

The container boots Postgres on its Unix socket, runs migrations, then exposes the receiver + dashboard on `:8080`. Watch stdout for the first-run setup token:

```
================================================================
 tamp-beacon — first-run setup token
----------------------------------------------------------------
 token:    <token>
================================================================
```

Consume it to mint the first sysadmin, then sign in at `http://localhost:8080`:

```bash
curl -sS -X POST http://localhost:8080/setup \
  -H 'content-type: application/json' \
  -d '{"token":"<token>","username":"admin","password":"correct-horse-battery-staple"}'
```

### Image tag scheme

| Tag | Lifecycle |
|---|---|
| `:0.1.0` | Immutable — published once on the `v0.1.0` tag, never overwritten |
| `:0.1` | Floats forward on each patch in the 0.1.x line |
| `:latest` | Floats forward on every non-prerelease tag |
| `:main` | Trunk image, pushed after every CI-green commit on `main` |

Adopters running production deploys should pin `:0.1.0`. Tracking `:0.1` or `:latest` keeps you on patches automatically.

### Running from source (contributor inner loop)

```bash
# 1. Bring up Postgres
docker run -d --name beacon-pg -p 5432:5432 \
  -e POSTGRES_USER=beacon -e POSTGRES_PASSWORD=beacon -e POSTGRES_DB=beacon \
  postgres:17-alpine

# 2. Build the SPA into wwwroot
(cd web && yarn install && yarn build)
rm -rf src/Tamp.Beacon/wwwroot && cp -r web/dist src/Tamp.Beacon/wwwroot

# 3. Run the beacon — same setup-token + /setup flow as the container path above
BEACON_DB_CONNECTION_STRING="Host=localhost;Username=beacon;Password=beacon;Database=beacon" \
  dotnet run --project src/Tamp.Beacon
```

### Frontend inner loop

```bash
# Dev with hot reload — vite proxies /api/* and /v1/* to the backend on :8080
(cd web && yarn dev)   # SPA on :5173, proxies API to :8080
```

### Endpoint surface

```
GET  /healthz                → 200 { status: "ok" }
GET  /readyz                 → 200 { status: "ready", setup_complete, awaiting_setup }
GET  /setup/status           → 200 { awaiting_setup, is_complete, token_issued_at }
POST /setup                  → 200 { username, display_name, created_at } | 400 | 401 | 409

POST /break-glass            → 200 + Set-Cookie | 400 | 401 | 429
POST /logout                 → 200
GET  /me                     → 200 { username, display_name, is_system_admin, last_login_at } | 401
GET  /signin/github          → 302 → GitHub | 404 (when OAuth not configured)
GET  /signin/github/callback → 302 home + Set-Cookie | 403 (not in allowlist)
POST /admin/recover          → 200 | 400 | 401

GET    /api/projects                          → 200 { projects: [...] }       (filtered by RBAC)
POST   /api/projects                          → 201 (creator becomes admin)   | 400 | 409
GET    /api/projects/{slug}                   → 200 { project }               | 404
PATCH  /api/projects/{slug}                   → 200                           | 403 | 404
DELETE /api/projects/{slug}                   → 204 (soft-archive)            | 403 | 404

GET    /api/projects/{slug}/members           → 200 { members: [...] }        | 404
POST   /api/projects/{slug}/members           → 201                           | 403 | 404 | 409
PATCH  /api/projects/{slug}/members/{id}      → 200                           | 403 | 404 | 409 (min-one-admin)
DELETE /api/projects/{slug}/members/{id}      → 204                           | 403 | 404 | 409 (min-one-admin)

GET    /api/projects/{slug}/tokens            → 200 { tokens: [...] }         | 403 | 404
POST   /api/projects/{slug}/tokens            → 201 { token: "<plaintext>", ... }  (shown ONCE)
DELETE /api/projects/{slug}/tokens/{id}       → 204

GET    /api/projects/{slug}/configs                          → 200 { configs: [...] }   (per-config rollups)
POST   /api/projects/{slug}/configs                          → 201
GET    /api/projects/{slug}/configs/{configSlug}             → 200 { config }           | 404
PATCH  /api/projects/{slug}/configs/{configSlug}             → 200                      | 403 | 404
DELETE /api/projects/{slug}/configs/{configSlug}             → 204 (soft-archive)
GET    /api/projects/{slug}/configs/{configSlug}/builds      → 200 { builds, next_seq }
GET    /api/projects/{slug}/configs/{configSlug}/targets/slowest   → 200 { targets: [...] }
GET    /api/projects/{slug}/configs/{configSlug}/targets/flakiest  → 200 { targets: [...] }

GET    /api/admin/users                       → 200 { users: [...] }          | 403
POST   /api/admin/users/{u}/promote           → 200                           | 403 | 404
POST   /api/admin/users/{u}/demote            → 200                           | 403 | 404 | 409 (min-one-sysadmin)
POST   /api/admin/users/{u}/disable           → 200                           | 403 | 404 | 409
POST   /api/admin/users/{u}/enable            → 200                           | 403 | 404

POST   /v1/traces                             → 200 { partialSuccess: { rejectedSpans: 0 } }
                                                                              | 400 (bad body) | 401 (no/bad/revoked token)
                                                                              | 422 (non-Tamp scope)
POST   /v1/metrics                            → 200 (acked + dropped pre-v0.2)

GET    /api/builds                            → 200 { builds: [...], next_seq } (RBAC-filtered)
       ?project=<slug>&outcome=success|failure&since_seq=N&limit=50          | 400 (bad outcome)
GET    /api/builds/{id}                       → 200 { build, targets, commands, events } | 404
GET    /api/projects/{slug}/builds            → 200 { builds, next_seq }     | 404 (non-member)
       ?since_seq=N&outcome=&limit=
GET    /api/projects/{slug}/targets/slowest   → 200 { targets: [{ name, avg_duration_ns, p95_duration_ns, samples }] }
       ?limit=20&since_unix_ns=N
GET    /api/projects/{slug}/targets/flakiest  → 200 { targets: [{ name, fail_rate, samples }] }
       ?limit=20&samples_min=3&since_unix_ns=N

GET    /api/push/vapid-public-key             → 200 { public_key }            (public, no auth)
GET    /api/projects/{slug}/push              → 200 { subscriptions: [...] } | 404 (non-member)
POST   /api/projects/{slug}/push/subscribe    → 201 { id, updated:false }
                                              → 200 { id, updated:true }  (re-subscribe upserts)
                                              → 400 (missing endpoint/keys) | 404 (non-member)
POST   /api/projects/{slug}/push/unsubscribe  → 204                           | 400 | 404
```

### OTLP ingest

```bash
# Configure Tamp.Telemetry on the emit side
export OTEL_EXPORTER_OTLP_ENDPOINT=https://beacon.example.com
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer tbk_<minted-from-the-tokens-endpoint>"
# Tamp.Telemetry defaults to protobuf — beacon accepts both x-protobuf and JSON.
```

Per-ingest, the beacon advances the token's `last_used_at` watermark so admins can spot stale or misconfigured runners.

### RBAC model

Non-members of a project see **404** on every project-specific route, not 403 — project existence is itself sensitive (SonarQube semantics). System admins bypass project-level checks and see / mutate every project. Min-one-admin invariants are enforced at the service layer (last project admin can't be demoted or removed; last system admin can't be demoted or disabled).

### Admin password recovery

```bash
# Inside the running pod (kubectl exec):
tamp-beacon admin recover --username scott
# Prints a one-shot reset token to stdout. TTL = 1h (configurable).

# Operator consumes via:
curl -X POST https://beacon.example.com/admin/recover \
  -H 'content-type: application/json' \
  -d '{"username":"scott","token":"<token>","new_password":"<new>"}'
```

## What's in v0.1.0

| Slice | Scope | Status |
|---|---|---|
| 1 | scaffold + bootstrap + health | shipped |
| 2 | GitHub OAuth login + cookie session + admin break-glass + admin recovery CLI | shipped |
| 3 | Project CRUD + RBAC (admin/viewer) + per-project ingest tokens + sysadmin promotion | shipped |
| 4 | OTLP/HTTP protobuf+JSON ingest gated by project tokens + Build→Project FK | shipped |
| 5a | Read API surface (builds list/detail + slowest/flakiest rollups) | shipped |
| 5b | SPA dashboard wired against the slice-5a API | shipped |
| 6 | Web Push failure alerts + slowest/flakiest UI cards | shipped |
| TAM-215 | BuildConfig layer (Project → BuildConfig → Build) | shipped |
| TAM-218 | Cross-batch trace_id reconcile (one build row per CI run) | shipped |

Deferred to a future release: arm64 image (`linux/arm64`), cosign signing, SLSA provenance, deeper docs (filed as `TAM-docs` backlog).

## Auth model (TAM-214)

Auth is enabled from line one — there is no "authless mode."

- **System Admin** — created during first-run setup via the stdout-printed token. Sees all projects.
- **Project Admin / Viewer** — SonarQube-style two-tier RBAC, per-project. Project Admins mint and rotate ingest tokens.
- **Federated identity** — GitHub OAuth for humans. Login + org allowlist enforced server-side; users not on the list get 403 from the callback. Admin break-glass via local username/password covers the "lost OAuth provider" failure mode.
- **Setup-token bootstrap** — printed to stdout at first boot; consumed by `POST /setup`. Restart the pod to mint a fresh token if the operator loses the original copy. Trust boundary = pod-log readership.
- **Admin recovery** — `tamp-beacon admin recover --username <name>` prints a one-shot password-reset token to stdout. Same trust model as setup-token (pod-log readership).

See the TAM-214 spec in the Tamp YouTrack for the complete threat model.

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│ Single container — ghcr.io/tamp-build/tamp-beacon:0.1.0          │
│                                                                  │
│  tini (PID 1) supervises:                                        │
│     postgres 17     (Unix-socket trust auth in /var/lib/...)     │
│     Tamp.Beacon     (.NET 10 / Kestrel on :8080)                 │
│                                                                  │
│  ASP.NET Core 10 host                                            │
│     /healthz, /readyz, /setup, /setup/status                     │
│     /api/* (admin + project + config CRUD, builds, rollups)      │
│     /v1/traces, /v1/metrics (OTLP/HTTP — protobuf + JSON)        │
│     /  (React SPA: builds, build detail, slowest/flakiest)       │
│     /api/push/subscribe (Web Push failure alerts)                │
│                                                                  │
│  Postgres schema (EF Core)                                       │
│     auth: users, projects, project_members, project_tokens,      │
│           identity_providers, identity_provider_links,           │
│           setup_state, auth_audit_log                            │
│     telemetry: build_configs, builds, targets, commands, events, │
│                push_subscriptions                                │
└──────────────────────────────────────────────────────────────────┘
```

External-Postgres mode: set `BEACON_DB_CONNECTION_STRING` and the bundled Postgres process is suppressed. Same image, just one PID under tini.

## Building locally

This repo dogfoods Tamp end-to-end. The `Build.cs` orchestrates Yarn install + Vite build, copies the SPA into `wwwroot/`, builds the .NET host, runs tests, publishes, builds the Docker image, and smoke-tests the resulting container. CI drives the same `Build.cs` through `dotnet tamp Test`, so every CI run emits its own build span to the lab beacon — the project is its own first adopter.

```bash
dotnet build Tamp.Beacon.slnx
dotnet test tests/Tamp.Beacon.Tests
dotnet run --project src/Tamp.Beacon

# Full Tamp dogfood — runs the same target graph CI runs.
dotnet tool install --global dotnet-tamp --version 1.0.0
dotnet tamp Test
```

## Related

- Emit-side quickstart: [`docs/observing-your-builds.md`](docs/observing-your-builds.md) — installing `Tamp.Telemetry`, env-var wiring, CI shape, Web Push opt-in
- Deploy reference: [`deploy/`](deploy/) — docker-compose for single-host, StatefulSet + Service + Ingress for Kubernetes
- Design sketch: [`tamp-beacon-v0.1.0.md`](https://github.com/tamp-build/tamp/blob/main/docs/sketches/tamp-beacon-v0.1.0.md) (in the Tamp repo)
- Auth spec: TAM-214 (private YouTrack — public summary forthcoming)
- Emission contract: [ADR 0018 — Diagnostics emission contract](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md)
