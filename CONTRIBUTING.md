# Contributing to tamp-beacon

Thanks for considering a contribution. `tamp-beacon` is the receiver and dashboard companion to [Tamp](https://github.com/tamp-build/tamp); the project conventions inherit from Tamp's. PRs that arrive with the conventions below get merged faster.

## Ground rules

- Be kind. The [Code of Conduct](CODE_OF_CONDUCT.md) applies in every issue, PR, and discussion.
- Architecture and naming conventions for the broader Tamp project are recorded in [`docs/adr/` in `tamp-build/tamp`](https://github.com/tamp-build/tamp/tree/main/docs/adr). The emission contract `tamp-beacon` receives is pinned by [ADR 0018](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md); changes to that contract belong upstream.
- Decisions evolve via successor ADRs. Don't argue with an Accepted ADR in a code-review thread; open a follow-up ADR proposal in the Tamp repo.

## Getting set up

`tamp-beacon` targets .NET 8, 9, and 10 — the host assembly multi-targets all three (matches Tamp's [target-framework strategy](https://github.com/tamp-build/tamp/blob/main/docs/adr/0015-target-framework-strategy.md)). You need all three SDKs installed locally to run the full test matrix.

```bash
# macOS — Microsoft pkg installers via Homebrew
brew install --cask dotnet-sdk@8 dotnet-sdk@9 dotnet-sdk

# Linux — Microsoft package feed (see https://learn.microsoft.com/dotnet/core/install/linux)
# Windows — winget install Microsoft.DotNet.SDK.8 / .9 / .10
```

Verify:

```bash
dotnet --list-sdks
# 8.0.x, 9.0.x, 10.0.x
```

Then:

```bash
git clone git@github.com:tamp-build/tamp-beacon.git
cd tamp-beacon
dotnet restore Tamp.Beacon.slnx
dotnet build Tamp.Beacon.slnx
dotnet test Tamp.Beacon.slnx

# Or drive the whole pipeline through Tamp's own dogfood:
dotnet tool install --global dotnet-tamp --version 1.0.0
dotnet tamp Test
```

A clean build is zero warnings, zero errors, every test green. CI enforces this on every PR.

## Repository layout

```
src/Tamp.Beacon/         production code — ASP.NET Core host, OTLP receiver, EF Core schema
tests/Tamp.Beacon.Tests/ xUnit integration tests against a Testcontainers-Postgres
web/                     React + Vite + Tailwind + shadcn/ui SPA, served from wwwroot/
build/Build.cs           Tamp build pipeline (dogfooded — same target graph CI runs)
scripts/                 entrypoint.sh and operational helpers baked into the image
.github/workflows/       CI definitions
```

The SPA is built by `yarn build` and copied into `src/Tamp.Beacon/wwwroot/` for the .NET host to serve as static assets. PRs that touch the frontend should run `yarn lint` and `yarn build` locally.

## Pull request flow

1. **Open an issue first** for anything more involved than a typo. The maintainer team uses YouTrack internally for work tracking — for outside contributors a GitHub issue is fine; the maintainer mirrors it as needed.
2. **Branch from `main`.** Topic branches; no long-lived feature branches.
3. **Keep PRs scoped.** A PR that touches the receiver, the SPA, and the build pipeline is harder to review than three focused ones.
4. **Tests are mandatory** for new behavior — boundary values, null/empty inputs, unicode, concurrency where applicable. The bar is "tests find bugs," not "tests cover lines." Receiver tests use `Testcontainers.PostgreSql` against a real Postgres instance, not mocks.
5. **Run the full pipeline locally** before pushing — `dotnet tamp Test`. CI will catch what you missed; saving CI cycles is polite.
6. **Commit messages** use a leading conventional-style prefix (`feat:`, `fix:`, `docs:`, `build:`, `ci:`, `chore:`, `refactor:`, `test:`). Body explains the *why*; the diff already shows the *what*.

## Proposing an ADR

Architectural decisions that affect the broader Tamp project (emission contract, target-framework strategy, governance) belong in the [`tamp-build/tamp` ADR set](https://github.com/tamp-build/tamp/tree/main/docs/adr). For decisions scoped to `tamp-beacon` (storage choices, dashboard surface, deployment shape), open an issue tagged `adr-proposal` first to confirm scope before drafting.

## Style and conventions

- **Editor:** Anything that respects `.editorconfig` and the central `Directory.Build.props` settings (`Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`).
- **Line length:** Soft limit ~100 chars. Don't reflow other people's code on unrelated edits.
- **Comments:** explain *why*, not *what*. The compiler reads the code; the next maintainer reads the comments.
- **Tests use xUnit + Bogus.** Theory tests for boundary cases. Avoid mocks where a real object will do.
- **Public API:** every public type or member needs an XML doc summary. Internal types are exempt unless they're load-bearing.
- **Don't add transitive dependencies casually.** `tamp-beacon` ships in a single image with bundled Postgres — every dependency lands in the image. New `<PackageVersion>` entries in `Directory.Packages.props` need a justification in the PR body.

## What we're NOT looking for

- Storage backends other than Postgres. Bundled Postgres + the external-Postgres `BEACON_DB_CONNECTION_STRING` escape hatch covers both self-host and shared-infra deployments.
- OTLP/gRPC. OTLP/HTTP is enough for Tamp's emit shape and dramatically simpler to terminate behind a reverse proxy.
- Multi-tenant separation beyond projects. RBAC stops at "project membership"; per-tenant isolation belongs at the deployment layer (one beacon per tenant).
- Plugin systems / receiver extensions. The receiver only accepts the [ADR 0018 emission contract](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md). If you want to ingest something else, write a separate receiver.

## Reporting security issues

See [`SECURITY.md`](SECURITY.md). Don't open public issues for vulnerabilities.

## Recognition

Substantial contributors are added to `MAINTAINERS.md` per [ADR 0009 §2.2](docs/adr/0009-governance-and-namespace-policy.md). The bar is sustained engagement and trust, not contribution count.
