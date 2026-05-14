#!/usr/bin/env bash
# tamp-beacon load generator. Walks every local tamp repo, runs `dotnet run
# --project build/Build.csproj -- <target>` with the OTel startup hook attached,
# and lets the build emit real spans to beacon's /v1/traces endpoint.
#
# Uses TampCoreMode=project so every satellite resolves Tamp.Core via the
# sibling ~/repos/tamp checkout (currently 1.4.0) — that's the version that
# ships the ADR 0018 emission contract. Without this, satellites pinned at
# 1.3.0 or earlier would compile but emit nothing.
#
# Usage:
#   tools/loadgen.sh [run_label]
#
# run_label (optional) is exported as OTEL_SERVICE_NAMESPACE so two runs are
# distinguishable on the dashboard (e.g. ./loadgen.sh runA  ./loadgen.sh runB).

set -uo pipefail

BEACON_URL="${BEACON_URL:-http://localhost:4318}"
RUN_LABEL="${1:-default}"
ORGANIZATION="${ORGANIZATION:-Tamp}"     # top-level grouping above project (forthcoming as [BuildProject(Organization=...)] in Tamp.Core 1.4.1)
HOOK_DLL="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/Tamp.Otel.StartupHook/bin/publish/Tamp.Otel.StartupHook.dll"
REPOS_ROOT="${REPOS_ROOT:-$HOME/repos}"
TARGET="${TARGET:-Compile}"

if [ ! -f "$HOOK_DLL" ]; then
    echo "hook DLL not found at $HOOK_DLL — build it first:"
    echo "  dotnet publish $(dirname "$HOOK_DLL")/.. -c Release -o $(dirname "$HOOK_DLL")"
    exit 1
fi

# Health-gate on beacon before doing anything.
if ! curl -sf "$BEACON_URL/healthz" >/dev/null; then
    echo "beacon not reachable at $BEACON_URL — start it first:"
    echo "  cd $REPOS_ROOT/tamp-beacon/src/Tamp.Beacon && dotnet run"
    exit 1
fi

# Repos to walk. tamp itself first (it dogfoods on its own Build.cs), then satellites.
# tamp-beacon excluded — running it would emit telemetry about itself emitting telemetry,
# which is funny once and noise forever.
REPOS=(
    tamp
    tamp-helm
    tamp-http
    tamp-templates
    tamp-docker tamp-turbo tamp-yarn tamp-vite tamp-playwright
    tamp-bicep tamp-codeql tamp-trufflehog tamp-sonar
    tamp-gh tamp-gitversion tamp-reportgenerator tamp-ef tamp-coverlet
    tamp-graphql-codegen
    tamp-azure-cli tamp-azure-functions-core-tools tamp-azure-static-web-apps
    tamp-ado-rest tamp-ado-service-connection tamp-servicebus tamp-testcontainers
)

printf "─── loadgen run '%s' targeting %s (%d repos) ───\n\n" "$RUN_LABEL" "$BEACON_URL" "${#REPOS[@]}"

passed=()
failed=()
skipped=()

for repo in "${REPOS[@]}"; do
    path="$REPOS_ROOT/$repo"
    if [ ! -d "$path/build" ]; then
        printf "  %-40s ⊘ skipped (no build/ dir)\n" "$repo"
        skipped+=("$repo")
        continue
    fi
    if [ ! -f "$path/build/Build.csproj" ]; then
        printf "  %-40s ⊘ skipped (no Build.csproj)\n" "$repo"
        skipped+=("$repo")
        continue
    fi

    # Per-repo logs go to a transient file; we tail the result.
    log="/tmp/loadgen-$repo-$$.log"

    DOTNET_STARTUP_HOOKS="$HOOK_DLL" \
    OTEL_EXPORTER_OTLP_ENDPOINT="$BEACON_URL" \
    OTEL_SERVICE_NAME="$repo" \
    OTEL_SERVICE_NAMESPACE="$RUN_LABEL" \
    OTEL_BUILD_ORGANIZATION="$ORGANIZATION" \
    TampCoreMode=project \
    dotnet run --project "$path/build/Build.csproj" -- "$TARGET" \
        > "$log" 2>&1
    code=$?

    if [ "$code" -eq 0 ]; then
        printf "  %-40s ✓\n" "$repo"
        passed+=("$repo")
    else
        printf "  %-40s ✗ exit %d (see %s)\n" "$repo" "$code" "$log"
        failed+=("$repo")
    fi
done

echo ""
printf "─── done: %d passed, %d failed, %d skipped ───\n" \
    "${#passed[@]}" "${#failed[@]}" "${#skipped[@]}"

if [ "${#failed[@]}" -gt 0 ]; then
    echo ""
    echo "failed repos:"
    for r in "${failed[@]}"; do echo "  - $r (/tmp/loadgen-$r-$$.log)"; done
fi

# Tell the user how to verify.
echo ""
echo "verify ingestion:"
echo "  curl -s $BEACON_URL/api/builds | jq '.builds | length'"
echo "  curl -s $BEACON_URL/api/projects | jq ."
