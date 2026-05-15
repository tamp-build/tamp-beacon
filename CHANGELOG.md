# Changelog

All notable changes to `tamp-beacon` are documented in this file.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning 2.0.0](https://semver.org/).

Pre-1.0 versions may break public API freely between minor versions; the `0.x` line is intentionally a stabilization run.

## [Unreleased]

## [0.1.1] - 2026-05-15

### Added

- **`linux/arm64` image** alongside the existing `linux/amd64`. The published image is now a multi-arch manifest — Apple-silicon Macs, AWS Graviton nodes, and Raspberry Pi 4/5 hosts can `docker pull ghcr.io/tamp-build/tamp-beacon:<tag>` without `--platform` flags. CI builds via cross-compiled `dotnet publish` (native, no emulation) plus QEMU for the Dockerfile's `apk add` / `chmod` steps.
- **Data-protection key-ring encryption at rest** (TAM-219). New `Beacon:Auth:KeyProtection` config section with three modes: `None` (lab default — boots with a loud startup WARNING), `SecretFile` (32-byte AES-256-GCM key loaded from disk; ship a Kubernetes Secret, point the beacon at it), and `X509` (PFX-based, framework-provided `CertificateXmlEncryptor`). Adopter-hosted production deploys no longer have to accept session-forgery risk from a stolen PVC snapshot. Wiring deferred via `IConfigureOptions<KeyManagementOptions>` so late-binding config providers (env vars, layered appsettings) win over the eager registration-time defaults.
- **Production checklist** (`docs/production-checklist.md`) — top item is the key-ring encryption setup; also covers HTTPS, external Postgres decision, PVC access discipline, recovery handle, and image-tag pinning.

### Fixed

- Data-protection wiring in `AddDataProtection` was reading `AuthOptions` via eager `config.Bind(...)` at service-registration time, which locked in stale defaults whenever a late-binding config provider (env vars, layered appsettings, `WebApplicationFactory` in-memory overlay) wanted to override. Refactored to `IConfigureOptions<KeyManagementOptions>` so binding happens at resolution time. Latent before TAM-219; surfaced while landing the encryption modes.

## [0.1.0] - 2026-05-15

First public release. Single-image OTLP receiver + dashboard for Tamp builds, shipped as `ghcr.io/tamp-build/tamp-beacon:0.1.0` (`linux/amd64`).

### Added

- **OTLP/HTTP ingest** on `/v1/traces` and `/v1/metrics` — protobuf primary, JSON tolerant. Source-name prefix gated (`Tamp.Build*`); non-Tamp telemetry rejected with HTTP 422. Per-project bearer tokens (`tbk_*`) gate every POST and advance a `last_used_at` watermark on each ingest.
- **Bundled Postgres 17** under tini supervision — the container is self-contained. Set `BEACON_DB_CONNECTION_STRING` to an external instance and the bundled process is suppressed. Persists to a single PVC mount at `/var/lib/tamp-beacon` (Postgres datadir + setup token + VAPID key).
- **Auth from day one** (TAM-214) — first-run setup token printed to stdout, sysadmin bootstrap via `POST /setup`, GitHub OAuth federated login with org-allowlist enforcement, username/password break-glass, project-scoped RBAC (Admin / Viewer) with SonarQube-style 404-on-non-membership semantics, min-one-admin invariants enforced at the service layer, sysadmin promotion + disable.
- **Project → BuildConfig → Build hierarchy** (TAM-215) — each project owns N build configs (`pr-validation`, `main-ci`, `nightly`, …); CI tags drive auto-create on first ingest. The dashboard's project screen lists configs with last-build / total-builds / CPU-time rollups; the config detail screen carries the slowest/flakiest target rankings.
- **Cross-batch trace_id reconcile** (TAM-218) — Target spans arriving in OTLP batches *before* their parent Build span no longer scatter across synthetic Build rows. The receiver upserts on `(project_id, trace_id)` and overlays late-arriving Build-span attrs onto the same row, so one CI run = one Build row regardless of batch ordering.
- **React + Vite + Tailwind + shadcn/ui SPA** served from `wwwroot/`. Pages: setup wizard, login, projects index, project detail (configs grid), config detail (recent builds + rollups), build detail (targets / commands / events tabs), project settings (notifications + members + tokens). React Query polling on a 5s cadence; monotonic `seq` cursor for delta refresh.
- **Web Push failure alerts** — VAPID keys auto-generate on first boot and persist to the PVC. Project members opt in per-browser via the project settings screen. A background worker fans out one notification per (project, target, coalesce-window) so a flake loop doesn't spam. Browser-specific subscription failures (Brave's default block, denied permission, missing keys) map to actionable error sentences in the UI.
- **Read API** under `/api/*` — list/detail builds with `since_seq` cursor, slowest + flakiest target rankings (per project and per config), members and tokens CRUD, sysadmin user management.
- **Admin recovery CLI** — `tamp-beacon admin recover --username <name>` prints a one-shot reset token to stdout; same trust model as the setup token (pod-log readership).
- **Dogfooded `Build.cs`** — Yarn install + build, copy SPA into wwwroot, .NET restore/build/test, self-contained publish for `linux-musl-x64`, Docker buildx, container smoke probe against `/healthz`. Tamp's own ActivitySources are wired via `Tamp.Telemetry.FromEnvironment()`, so every local + CI build of tamp-beacon emits its own spans to the lab beacon.
- **CI/CD** — `ci.yml` runs the whole pipeline through `dotnet tamp Test` (Restore → YarnInstall → FrontendBuild → CopyWwwroot → Compile → Test, each emitting its own target span). `release.yml` fires on `v*` tags and pushes `:{version}`, `:{minor}`, `:latest` (non-prerelease only) to ghcr.io. `main-push.yml` fires on CI-green main commits and pushes `:main`.

### Notes

- `linux/arm64` image is deferred to v0.2. Apple-silicon users can `docker pull --platform linux/amd64` and run under Rosetta.
- `OTLP/gRPC`, retention policies, anomaly detection, webhook integrations, multi-tenant separation beyond projects, cosign signing, and SLSA provenance attestations are deferred to 0.2+.
- The original v0.1.0 plan included a `Tamp.Beacon.Sdk` companion NuGet (typed C# client); it was cut during the v0.1 sweep — `Tamp.Telemetry` is the only NuGet adopters install.

[Unreleased]: https://github.com/tamp-build/tamp-beacon/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/tamp-build/tamp-beacon/releases/tag/v0.1.1
[0.1.0]: https://github.com/tamp-build/tamp-beacon/releases/tag/v0.1.0
