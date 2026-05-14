# tamp-beacon

**A self-hosted, single-image OpenTelemetry receiver and dashboard for [Tamp](https://github.com/tamp-build/tamp) builds.**

Tamp builds emit a pinned diagnostics contract (ADR 0018) — three `ActivitySource`s and one `Meter`, with ~80 stable tag keys covering build / target / command spans plus host and CI facets. `tamp-beacon` receives exactly that contract and gives you a queryable history, a browser dashboard, and Web Push notifications on build failure. **One container, bundled Postgres, backed up by a single PVC snapshot.**

| | |
|---|---|
| Image | `ghcr.io/tamp-build/tamp-beacon:0.1.0` (unreleased — slice 1 in progress) |
| Port | `8080` (OTLP/HTTP receiver + dashboard + admin UI on the same port) |
| Volume | `/var/lib/tamp-beacon` (Postgres datadir + setup token + VAPID key) |
| Status | preview — slice-1 in progress, see "Build slices" below |

## Slice-2 status (you are here)

This branch ships **slice 1 (scaffold + bootstrap + health) + slice 2 (cookie auth + GitHub OAuth + admin recovery)**. The auth surface is feature-complete from line one: local admin break-glass login over an argon2id-hashed password, GitHub OAuth sign-in with login + org allowlist, sliding-window cookie sessions backed by data-protection keys persisted to the PVC, and a `kubectl exec`-driven password-recovery CLI that mints a one-shot reset token. OTLP receivers, dashboards, and Web Push come online in later slices.

### Running slice 1 locally

```bash
# 1. Bring up Postgres
docker run -d --name beacon-pg -p 5432:5432 \
  -e POSTGRES_USER=beacon -e POSTGRES_PASSWORD=beacon -e POSTGRES_DB=beacon \
  postgres:17-alpine

# 2. Build + run the beacon
BEACON_DB_CONNECTION_STRING="Host=localhost;Username=beacon;Password=beacon;Database=beacon" \
  dotnet run --project src/Tamp.Beacon

# 3. Watch stdout for the first-run setup token banner:
#
#    ================================================================
#     tamp-beacon — first-run setup token
#    ----------------------------------------------------------------
#     token:    <token>
#    ================================================================
#
# 4. Consume the token to mint the first admin
curl -sS -X POST http://localhost:8080/setup \
  -H 'content-type: application/json' \
  -d '{"token":"<token>","username":"admin","password":"correct-horse-battery-staple"}'
```

### Endpoint surface (slices 1 + 2)

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
```

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

## Build slices

| Slice | Scope | Status |
|---|---|---|
| 1 | scaffold + bootstrap + health | shipped |
| 2 | GitHub OAuth login + cookie session + admin break-glass + admin recovery CLI | shipped |
| 3 | Project CRUD + RBAC (admin/viewer) + per-project ingest tokens | pending |
| 4 | OTLP/HTTP `/v1/traces` + `/v1/metrics` ingest gated by project tokens | pending |
| 5 | SPA dashboard (builds list, drill-down, slow/flaky targets) | pending |
| 6 | Web Push failure alerts (VAPID, project-scoped) | pending |
| 7 | Docs + on-ramp polish + 1.0 cut | pending |

## Auth model (TAM-214)

Auth is enabled from line one — there is no "authless mode."

- **System Admin** — created during first-run setup via the stdout-printed token. Sees all projects.
- **Project Admin / Viewer** — SonarQube-style two-tier RBAC, per-project. Project Admins mint and rotate ingest tokens.
- **Federated identity** — GitHub OAuth for humans. Login + org allowlist enforced server-side; users not on the list get 403 from the callback. Admin break-glass via local username/password covers the "lost OAuth provider" failure mode.
- **Setup-token bootstrap** — printed to stdout at first boot; consumed by `POST /setup`. Restart the pod to mint a fresh token if the operator loses the original copy. Trust boundary = pod-log readership.
- **Admin recovery** — `tamp-beacon admin recover --username <name>` prints a one-shot password-reset token to stdout. Same trust model as setup-token (pod-log readership).

See the TAM-214 spec in the Tamp YouTrack for the complete threat model.

## Architecture (target — slice 1 ships the boxes marked ◯)

```
┌──────────────────────────────────────────────────────────────────┐
│ Single container — ghcr.io/tamp-build/tamp-beacon:0.1.0          │
│                                                                  │
│  ◯ tini (PID 1) supervises:                                      │
│     ◯ postgres 17    (Unix-socket trust auth in /var/lib/...)    │
│     ◯ Tamp.Beacon    (.NET 10 / Kestrel on :8080)                │
│                                                                  │
│  ◯ ASP.NET Core 10 host                                          │
│     ◯ /healthz, /readyz, /setup, /setup/status                   │
│     △ /api/* (admin + project CRUD — slice 3)                    │
│     △ /v1/traces, /v1/metrics (OTLP/HTTP — slice 4)              │
│     △ /  (dashboard SPA — slice 5)                               │
│     △ /api/push/subscribe (Web Push — slice 6)                   │
│                                                                  │
│  ◯ Postgres schema (EF Core)                                     │
│     ◯ auth: users, projects, project_members, project_tokens,    │
│            identity_providers, identity_provider_links,          │
│            setup_state, auth_audit_log                           │
│     ◯ telemetry: builds, targets, commands, events,              │
│            push_subscriptions (mapped; ingest in slice 4)        │
└──────────────────────────────────────────────────────────────────┘
```

External-Postgres mode: set `BEACON_DB_CONNECTION_STRING` and the bundled Postgres process is suppressed. Same image, just one PID under tini.

## Building locally

This repo dogfoods Tamp end-to-end. The `Build.cs` orchestrates Yarn install + Vite build, copies the SPA into `wwwroot/`, builds the .NET host, runs tests, publishes, builds the multi-arch Docker image, and smoke-tests the resulting container.

```bash
# Slice-1 inner loop:
dotnet build Tamp.Beacon.slnx
dotnet test tests/Tamp.Beacon.Tests
dotnet run --project src/Tamp.Beacon
```

## Related

- Design sketch: [`tamp-beacon-v0.1.0.md`](https://github.com/tamp-build/tamp/blob/main/docs/sketches/tamp-beacon-v0.1.0.md) (in the Tamp repo)
- Auth spec: TAM-214 (private YouTrack — public summary forthcoming)
- Emission contract: [ADR 0018 — Diagnostics emission contract](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md)
- Wiki: **Observing-Your-Builds** (forthcoming — covers `Tamp.Telemetry` install + beacon URL config + dashboard tour)
