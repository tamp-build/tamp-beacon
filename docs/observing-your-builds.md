# Observing your Tamp builds

You stood up a `tamp-beacon` instance — now you want your Tamp builds to actually report into it. This page walks the emit side: installing `Tamp.Telemetry`, pointing it at your beacon, and confirming the spans land.

> This page is the v0.1.0 quickstart. For deeper context — what `tamp-beacon` actually stores, what the dashboard rollups mean, what to expect under high-volume CI — see the [README](https://github.com/tamp-build/tamp-beacon#readme) and the [emission contract (ADR 0018)](https://github.com/tamp-build/tamp/blob/main/docs/adr/0018-diagnostics-emission-contract.md).

## Prerequisites

- A running `tamp-beacon` (any of the deploy shapes in [`deploy/`](../deploy/) work)
- A Tamp project that already builds via `dotnet tamp <Target>`
- Sysadmin or Project Admin credentials on the beacon (you set the sysadmin during first-run setup)

## 1. Create a project and mint an ingest token

Sign into the beacon at `https://<your-beacon-host>/`. Create a project (slug is what you'll reference in `OTEL_SERVICE_NAME`), then **Settings → Tokens → New token**. Give it a descriptive label (e.g. `ci-runner`, `dev-laptop-scott`); the plaintext token is shown **once** — copy it now. It always starts with `tbk_`.

```
tbk_bfKct6FzLCNCaqNmtlLZvG5EqZDnDIudiBJqVCBn9hs
```

Lost the token? Mint a new one — tokens are individually revocable, so over-issuing isn't expensive.

## 2. Install Tamp.Telemetry on the emit side

In your project's `build/Build.csproj`:

```xml
<PackageReference Include="Tamp.Telemetry" Version="0.1.2" />
```

`Tamp.Telemetry` reads the canonical OpenTelemetry env vars (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`). With those env vars unset, telemetry is a no-op — the same `Build.cs` works locally without a beacon.

## 3. Wire the build entrypoint

```csharp
using Tamp.Telemetry;

class Build : TampBuild
{
    public static int Main(string[] args)
    {
        using var telemetry = TampTelemetry.FromEnvironment();
        return Execute<Build>(args);
    }

    // ... your targets ...
}
```

The `using` is load-bearing — `TampTelemetry.Dispose()` is what flushes the final batch on process exit. Drop it and your last build's spans never leave the runner.

## 4. Set the env vars

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="https://beacon.example.com"
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer tbk_<your-minted-token>"
export OTEL_SERVICE_NAME="<project-slug>"
export TAMP_BUILD_CONFIG_NAME="dev-laptop"   # or "main-ci", "pr-validation", "nightly", ...
```

The endpoint is the beacon root; `Tamp.Telemetry` appends `/v1/traces` itself.

**`TAMP_BUILD_CONFIG_NAME`** is the per-build-shape label — the beacon will auto-create a `BuildConfig` on first ingest under this name. Common shapes:

| Where the build runs | Suggested `TAMP_BUILD_CONFIG_NAME` |
|---|---|
| PR validation in CI | `pr-validation` |
| Main-branch CI | `main-ci` |
| Nightly schedule | `nightly` |
| Developer's local machine | `dev-laptop` (or a per-dev label like `dev-laptop-scott`) |
| Release tag pipeline | `release` |

If you set nothing, ingest lands under a `default` config — fine for a single-shape project, but you'll lose the per-shape rollups (e.g. main-CI red rate vs. PR red rate).

## 5. Confirm the spans land

Run a build:

```bash
dotnet tamp Test
```

Within 5 seconds (the `BatchExportProcessor` scheduled-flush interval) you should see a new row on the project's dashboard. Each Tamp target (`Restore`, `Compile`, `Test`, …) becomes its own child span; the dashboard's **slowest** and **flakiest** rollups break down per-target.

If nothing shows up:

- **401 on `/v1/traces`** — token is wrong, revoked, or missing the `Bearer` prefix. Re-check the `Authorization=Bearer ...` shape.
- **422 on `/v1/traces`** — the spans are arriving from a non-Tamp `ActivitySource`. `tamp-beacon` only accepts the `Tamp.Build*` scope set (per ADR 0018). Make sure you're not subscribing additional sources upstream of `TampTelemetry.FromEnvironment()`.
- **`POST /` instead of `POST /v1/traces` in the beacon logs** — you're on `Tamp.Telemetry < 0.1.2`. Upgrade.
- **Spans show as separate Build rows per CI run** — you're on a beacon < 0.1.0 build that predates the TAM-218 cross-batch reconcile fix.

## 6. CI wiring (GitHub Actions)

Set the endpoint as a non-secret **Variable** and the headers as a **Secret**:

```yaml
env:
  OTEL_EXPORTER_OTLP_ENDPOINT: ${{ vars.OTEL_EXPORTER_OTLP_ENDPOINT }}
  OTEL_EXPORTER_OTLP_HEADERS: ${{ secrets.OTEL_EXPORTER_OTLP_HEADERS }}
  OTEL_SERVICE_NAME: my-project
  TAMP_BUILD_CONFIG_NAME: ${{ github.event_name == 'pull_request' && 'pr-validation' || 'main-ci' }}
```

The conditional `TAMP_BUILD_CONFIG_NAME` gives you separate rollup rows for PR builds vs. main-branch builds without changing your `Build.cs`.

## 7. Web Push failure alerts (optional)

In the beacon SPA, open your project's **Settings → Notifications** card and click **Enable failure alerts**. The browser prompts for permission; tap allow. Future builds that hit a failure in your project will push a notification to the device — coalesced so a flake loop doesn't spam you.

Per-browser caveat: Brave blocks the `pushManager.subscribe()` API by default — see `brave://settings/privacy` → "Use Google services for push messaging" → on. The SPA detects this case and shows an actionable error sentence pointing at the right toggle.

## What's next

- **Add more projects.** Each Tamp project is a separate beacon project; one beacon can host many.
- **Cut your CI rollups into shapes.** Use `TAMP_BUILD_CONFIG_NAME` aggressively — `pr-validation` vs `main-ci` vs `nightly` vs `release`. The per-config slowest/flakiest views surface different signal at each shape.
- **Pin the image.** Production beacons should pin `:0.1.0` rather than `:latest`. See the [README's tag scheme table](https://github.com/tamp-build/tamp-beacon#image-tag-scheme).
