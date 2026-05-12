# Changelog

All notable changes to `tamp-beacon` are documented in this file.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning 2.0.0](https://semver.org/).

Pre-1.0 versions may break public API freely between minor versions; the `0.x` line is intentionally a stabilization run.

## [Unreleased]

## [0.1.0] - 2026-05-12

### Added

- Single-image OTel receiver + dashboard for Tamp builds (`ghcr.io/tamp-build/tamp-beacon:0.1.0`).
- **OTLP/HTTP-JSON receiver** on `/v1/traces` and `/v1/metrics`. Validates the source-name prefix (`Tamp.Build*`); non-Tamp telemetry is rejected with HTTP 422.
- **SQLite storage** at `/var/lib/tamp-beacon/db.sqlite` with WAL mode. Schema covers builds, targets, commands, events, and Web Push subscriptions. Indexed columns for the hot dashboard queries; `raw_tags` JSON column captures every ADR-0018 tag without a migration on the next tag addition.
- **HTTP/JSON query API** under `/api/*` — list/detail builds with monotonic `seq` cursor for delta polling, slowest + flakiest target rankings, project + area pivots, push-subscription registration, `/healthz` with VAPID public key.
- **React + Vite + Tailwind + shadcn/ui SPA** served from `wwwroot/`. Pages: Builds, Build detail, Targets, Projects, Alerts. React Query polling on a 5s default cadence.
- **Web Push** notifications for build failures. VAPID keys auto-generated on first boot and persisted under `/var/lib/tamp-beacon/vapid.key`. Coalesced by project + target + 5-minute window so a flaky failure loop doesn't spam.
- **Tamp.Beacon.Sdk** companion NuGet package — typed C# client over the JSON API (`net8.0;net9.0;net10.0`).
- **Dogfooded Build.cs** using `Tamp.Yarn.V4`, `Tamp.Vite.V5`, `Tamp.NetCli.V10`, `Tamp.Docker.V27`, `Tamp.Http`. Multi-arch image build (linux/amd64, linux/arm64) via Docker buildx; smoke-tests the resulting image against `/healthz` + a sample OTLP payload.

### Notes

- Authless v0.1.0 — designed for trusted-network deployment behind a reverse proxy or Cloudflare tunnel. Token auth reserved for 0.2.0.
- OTLP/gRPC, multi-tenant separation, retention policies, anomaly detection, and webhook integrations are deferred to 0.2.0+.

[Unreleased]: https://github.com/tamp-build/tamp-beacon/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/tamp-build/tamp-beacon/releases/tag/v0.1.0
