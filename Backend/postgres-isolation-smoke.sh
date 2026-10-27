#!/usr/bin/env bash
set -euo pipefail

[[ "${BARKCLOUD_ISOLATED_INFRA:-}" == YES ]] || {
  echo "Refusing PostgreSQL smoke checks without BARKCLOUD_ISOLATED_INFRA=YES." >&2
  exit 2
}
[[ "${BARKCLOUD_APPS_STOPPED:-}" == YES ]] || {
  echo "Stop backend applications and set BARKCLOUD_APPS_STOPPED=YES." >&2
  exit 2
}
docker compose config --services 2>/dev/null | grep -qx 'cloud-postgres' || {
  echo "This smoke script requires the isolated cloud-postgres Compose service." >&2
  exit 2
}

declare -A dbs=(
  [configuration]=barkcloud_configuration
  [identity]=barkcloud_identity
  [users]=barkcloud_users
  [files]=barkcloud_files
  [torrent]=barkcloud_torrent
)
declare -A passwords=(
  [configuration]=${CONFIGURATION_DB_PASSWORD:?export Configuration DB password}
  [identity]=${IDENTITY_DB_PASSWORD:?export Identity DB password}
  [users]=${USERS_DB_PASSWORD:?export Users DB password}
  [files]=${FILES_DB_PASSWORD:?export Files DB password}
  [torrent]=${TORRENT_DB_PASSWORD:?export Torrent DB password}
)

for own_db in configuration identity users files torrent; do
  role=${dbs[$own_db]}
  password=${passwords[$own_db]}
  safe_query="SELECT CASE WHEN rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls THEN 1 ELSE 0 END FROM pg_roles WHERE rolname = current_user"
  result=$(docker compose exec -T -e "PGPASSWORD=$password" cloud-postgres \
    psql -X -h 127.0.0.1 -U "$role" -d "$own_db" -tA -c "$safe_query")
  [[ "$result" == 1 ]] || { echo "FAIL PostgreSQL role attributes: $own_db" >&2; exit 1; }
  echo "PASS PostgreSQL own database: $own_db"

  for other_db in configuration identity users files torrent; do
    [[ "$own_db" != "$other_db" ]] || continue
    if docker compose exec -T -e "PGPASSWORD=$password" cloud-postgres \
      psql -X -h 127.0.0.1 -U "$role" -d "$other_db" -tA -c 'SELECT 1' >/dev/null 2>&1; then
      echo "FAIL PostgreSQL cross-database access: $role connected to $other_db" >&2
      exit 1
    fi
  done
  echo "PASS PostgreSQL cross-database denial: $own_db"
done
