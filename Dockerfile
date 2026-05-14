# tamp-beacon — single-image deploy: Postgres 17 bundled with the
# ASP.NET Core 10 receiver/dashboard. Adopters who prefer to point at an
# external Postgres set BEACON_DB_CONNECTION_STRING and the bundled
# instance never starts.
#
# Build context expects ./publish/ to contain the self-contained
# linux-x64 / linux-arm64 publish output for Tamp.Beacon (produced by
# `tamp Publish` running `dotnet publish -r linux-<rid> --self-contained`).
#
# Process model: tini as PID 1, supervising:
#   * postgres (only when BEACON_DB_CONNECTION_STRING is not externally set)
#   * tamp-beacon dotnet host
# Both share the /var/lib/tamp-beacon PVC for state.

FROM postgres:17-alpine AS final

# Runtime deps for the self-contained .NET binary on Alpine.
RUN apk add --no-cache \
    tini \
    icu-libs \
    libstdc++ \
    bash \
    coreutils

WORKDIR /app

# Self-contained .NET publish output (no shared framework required).
COPY publish/ /app/

# Bundled-Postgres entrypoint and per-process supervisors.
COPY scripts/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /app/Tamp.Beacon

# Single PVC mount point — pgdata + setup.token + vapid.key all live here.
RUN mkdir -p /var/lib/tamp-beacon \
    && chown -R postgres:postgres /var/lib/tamp-beacon
VOLUME ["/var/lib/tamp-beacon"]

# Bundled Postgres listens only on the Unix socket; the receiver binds 8080.
ENV PGDATA=/var/lib/tamp-beacon/postgres \
    BEACON_SOCKET_DIR=/var/lib/tamp-beacon \
    ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s \
    CMD ["/app/Tamp.Beacon", "--healthcheck"]

ENTRYPOINT ["/sbin/tini", "--", "/usr/local/bin/entrypoint.sh"]
