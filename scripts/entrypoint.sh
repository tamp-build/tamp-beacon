#!/usr/bin/env bash
#
# tamp-beacon container entrypoint. Two roles depending on whether the
# operator wants the bundled Postgres or has wired an external one:
#
#   1. BEACON_DB_CONNECTION_STRING is unset/empty
#      → Initialize + start the bundled Postgres on the Unix socket at
#        $BEACON_SOCKET_DIR, then start the beacon pointed at it.
#
#   2. BEACON_DB_CONNECTION_STRING is set
#      → Skip bundled Postgres entirely; just start the beacon.
#
# Both processes are children of tini (the container's PID 1), so signal
# handling + zombie reaping are handled upstream.

set -euo pipefail

PG_USER="${POSTGRES_USER:-beacon}"
PG_DB="${POSTGRES_DB:-beacon}"
PGDATA="${PGDATA:-/var/lib/tamp-beacon/postgres}"
SOCKET_DIR="${BEACON_SOCKET_DIR:-/var/lib/tamp-beacon}"

start_bundled_pg() {
    mkdir -p "$PGDATA" "$SOCKET_DIR"
    chown -R postgres:postgres "$PGDATA" "$SOCKET_DIR" || true
    chmod 700 "$PGDATA"

    if [ ! -s "$PGDATA/PG_VERSION" ]; then
        echo "[entrypoint] initializing bundled postgres cluster at $PGDATA"
        su -s /bin/bash postgres -c "initdb --auth-local=trust --auth-host=reject \
            --username=postgres -D '$PGDATA' --encoding=UTF8"

        # Create the beacon role + database. Done via a single-user backend
        # so we don't need to bring up a network listener.
        su -s /bin/bash postgres -c "postgres --single -D '$PGDATA' postgres" <<SQL
CREATE ROLE $PG_USER WITH LOGIN SUPERUSER;
CREATE DATABASE $PG_DB WITH OWNER $PG_USER;
SQL
    fi

    echo "[entrypoint] starting bundled postgres on socket $SOCKET_DIR"
    su -s /bin/bash postgres -c "postgres -D '$PGDATA' \
        -c listen_addresses='' \
        -c unix_socket_directories='$SOCKET_DIR' \
        -c log_destination='stderr' \
        -c log_line_prefix='pg %t '" &
    PG_PID=$!

    # Wait for socket readiness.
    for _ in $(seq 1 30); do
        if su -s /bin/bash postgres -c "pg_isready -h '$SOCKET_DIR' -U '$PG_USER' -d '$PG_DB'" >/dev/null 2>&1; then
            echo "[entrypoint] postgres ready"
            return 0
        fi
        sleep 1
    done
    echo "[entrypoint] postgres failed to come up within 30s" >&2
    kill -TERM "$PG_PID" 2>/dev/null || true
    return 1
}

# Forward SIGTERM to children so kubectl drain doesn't have to wait for
# tini's grace timeout.
shutdown() {
    echo "[entrypoint] SIGTERM received — shutting down children"
    [ -n "${BEACON_PID:-}" ] && kill -TERM "$BEACON_PID" 2>/dev/null || true
    [ -n "${PG_PID:-}" ] && kill -TERM "$PG_PID" 2>/dev/null || true
    wait
    exit 0
}
trap shutdown TERM INT

if [ -z "${BEACON_DB_CONNECTION_STRING:-}" ]; then
    start_bundled_pg
    export BEACON_DB_CONNECTION_STRING="Host=$SOCKET_DIR;Username=$PG_USER;Database=$PG_DB"
else
    echo "[entrypoint] external Postgres mode (BEACON_DB_CONNECTION_STRING is set); bundled instance suppressed"
fi

echo "[entrypoint] starting Tamp.Beacon"
/app/Tamp.Beacon &
BEACON_PID=$!

wait -n "$BEACON_PID" ${PG_PID:+"$PG_PID"}
exit_code=$?
echo "[entrypoint] a child exited with $exit_code; tearing down"
shutdown
