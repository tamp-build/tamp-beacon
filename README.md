# tamp-beacon

**A self-hosted, single-image OpenTelemetry receiver and dashboard for [Tamp](https://github.com/tamp-build/tamp) builds.**

Tamp builds emit a pinned diagnostics contract (ADR 0018) — three `ActivitySource`s and one `Meter`, with ~80 stable tag keys covering build / target / command spans plus host and CI facets. `tamp-beacon` receives exactly that contract and gives you a queryable history, a browser dashboard, and Web Push notifications on build failure. **One container, one SQLite file, backed up by `cp`.**

| | |
|---|---|
| Image | `ghcr.io/tamp-build/tamp-beacon:0.1.0` |
| Port | `4318` (OTLP/HTTP-JSON default; dashboard on same port at `/`) |
| Volume | `/var/lib/tamp-beacon` (SQLite + VAPID key) |
| Status | preview — v0.1.0 |

## Three-line on-ramp

```bash
mkdir -p ./beacon-data
docker run -d -p 4318:4318 -v $PWD/beacon-data:/var/lib/tamp-beacon ghcr.io/tamp-build/tamp-beacon:0.1.0
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
```

Then point every developer's Tamp at `$OTEL_EXPORTER_OTLP_ENDPOINT` (via the forthcoming `Tamp.Otel` satellite) and open `http://localhost:4318` in a browser.

> **v0.1.0 scope:** **local-only exercise.** Public GitHub Actions runners can't reach a self-hosted beacon's ingress without a tunnel + auth — that wiring lands in a later wave. Right now, point your local dev runs and any private/self-hosted runners at the beacon; skip the OTel exporter env var on public CI workflows.

## What ships in v0.1.0

**In:**
- OTLP/HTTP-JSON receiver on `/v1/traces` and `/v1/metrics`.
- SQLite storage at `/var/lib/tamp-beacon/db.sqlite` with WAL mode.
- HTTP/JSON query API under `/api/*` (builds list/detail, projects, slowest/flakiest targets, push subscribe, healthz).
- React + Vite + Tailwind + shadcn/ui SPA served from `wwwroot/`, polling on 5s default cadence.
- Web Push (VAPID) failure alerts, coalesced by project + target + 5-min window.
- Filters by `tamp.build.project.name` and `tamp.build.project.area` — the polyrepo case Just Works.
- Authless. Designed for trusted-network deployment behind a reverse proxy / Cloudflare tunnel.

**Out (deferred to 0.2.0+):**
- OTLP/gRPC receiver
- Multi-tenant separation
- Retention / rollup jobs
- Webhook integrations (Slack, Teams)
- Token auth / OIDC
- SSE / WebSocket streaming (polling-only per the no-streaming policy)

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Single Docker image  ghcr.io/tamp-build/tamp-beacon:0.1.0  │
│                                                             │
│  ┌────────────────────────────────────────────────────┐     │
│  │  .NET 10 host (Kestrel)                            │     │
│  │   ├─ POST /v1/traces       (OTLP/HTTP-JSON)        │     │
│  │   ├─ POST /v1/metrics      (OTLP/HTTP-JSON)        │     │
│  │   ├─ GET  /api/builds      (filterable, paginated) │     │
│  │   ├─ GET  /api/builds/{id} (build + targets + cmds)│     │
│  │   ├─ GET  /api/projects    (distinct names+areas)  │     │
│  │   ├─ GET  /api/targets/slowest                     │     │
│  │   ├─ GET  /api/targets/flakiest                    │     │
│  │   ├─ POST /api/push/subscribe (VAPID register)     │     │
│  │   ├─ /  ←── wwwroot/ (React SPA, static-served)    │     │
│  │   └─ /healthz                                      │     │
│  │                                                    │     │
│  │  ┌──────────────────────────────────────────────┐  │     │
│  │  │ SQLite — /var/lib/tamp-beacon/db.sqlite      │  │     │
│  │  │  builds, targets, commands, events, push_subs│  │     │
│  │  └──────────────────────────────────────────────┘  │     │
│  └────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────┘
```

## API shape (selected)

```
GET /api/builds?project=HoldFast&area=frontend&since_seq=12345&limit=50
    → { builds: [...], next_seq: 12410 }

GET /api/builds/{id}
    → { build, targets: [...], commands: [...], events: [...] }

GET /api/projects
    → { projects: [{name, area, last_seen_unix_ns, builds_count}] }

GET /healthz
    → 200 { status: "ok", db_path, rows_total, vapid_public_key }
```

## Building locally

This repo dogfoods Tamp end-to-end. The `Build.cs` orchestrates Yarn install + Vite build, copies the SPA into `wwwroot/`, builds the .NET host, runs tests, publishes, builds the multi-arch Docker image, and smoke-tests the resulting container.

```bash
# Inner loop (sibling Tamp.Core via ProjectReference):
TampCoreMode=project dotnet build Tamp.Beacon.slnx

# CI / release loop (Tamp.Core from nuget.org):
TampCoreMode=package dotnet build Tamp.Beacon.slnx

# Full dogfood build (requires yarn + docker installed):
dotnet tamp Ci
```

## Web Push setup

VAPID keys are auto-generated on first boot and stored at `/var/lib/tamp-beacon/vapid.key`. The public key is published via `/healthz` so the SPA can register subscriptions without any manual key entry. Back up the VAPID key file alongside `db.sqlite` — same `cp` line, same restore path.

## Related

- Design sketch: [`tamp-beacon-v0.1.0.md`](https://github.com/tamp-build/tamp/blob/main/docs/sketches/tamp-beacon-v0.1.0.md) (in the Tamp repo)
- Emission contract: [ADR 0018 — Diagnostics emission contract](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md)
- Wiki: **Observing-Your-Builds** (forthcoming — covers `Tamp.Otel` SDK install + beacon URL config + dashboard tour)
