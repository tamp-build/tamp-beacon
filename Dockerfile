# tamp-beacon — single-image OTel receiver + dashboard for Tamp builds.
#
# Build context: the artifacts/publish/ directory produced by `tamp Publish`
# (Build.cs runs `dotnet publish -r linux-{amd,arm}64 --self-contained` into
# artifacts/publish/<rid>/ then buildx copies the rid-specific folder).
#
# The host binary is self-contained, so the chiseled runtime-deps image is
# sufficient — no ASP.NET Core sharing needed.

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS base
WORKDIR /app
EXPOSE 4318
ENV ASPNETCORE_URLS=http://+:4318
ENV BEACON_DB_PATH=/var/lib/tamp-beacon/db.sqlite
ENV BEACON_VAPID_KEY_PATH=/var/lib/tamp-beacon/vapid.key
VOLUME ["/var/lib/tamp-beacon"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
    CMD ["/app/Tamp.Beacon", "--healthcheck"]

FROM base AS final
COPY publish/ ./
ENTRYPOINT ["./Tamp.Beacon"]
