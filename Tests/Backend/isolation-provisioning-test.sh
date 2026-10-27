#!/usr/bin/env bash
set -euo pipefail

test_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$test_dir/../.." && pwd)
temp_root=$(mktemp -d "${TMPDIR:-/tmp}/barkcloud-isolation-provision.XXXXXX")
cleanup() {
  case "$temp_root" in
    "${TMPDIR:-/tmp}"/barkcloud-isolation-provision.*) rm -rf -- "$temp_root" ;;
    *) echo "Unexpected test directory; not removing it." >&2; return 1 ;;
  esac
}
trap cleanup EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

for script in \
  "$repo_root/Backend/provision-isolation.sh" \
  "$repo_root/Backend/provision-rabbitmq.sh" \
  "$repo_root/Backend/postgres-isolation-smoke.sh" \
  "$repo_root/Backend/rabbitmq-isolation-smoke.sh"; do
  bash -n "$script" || fail "shell syntax failed: ${script##*/}"
done

mkdir -p "$temp_root/bin"
cat > "$temp_root/bin/docker" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$BARKCLOUD_FAKE_DOCKER_LOG"
if [[ "${BARKCLOUD_FAKE_DOCKER_COMPOSE:-}" == YES ]]; then
  if [[ "$*" == "compose config --services" ]]; then
    printf 'cloud-postgres\ncloud-rabbitmq\n'
    exit 0
  fi
  if [[ "$*" == *"cloud-postgres"*"psql"* ]]; then exit 0; fi
fi
exit 1
SH
cat > "$temp_root/bin/rabbitmqctl" <<'SH'
#!/usr/bin/env bash
case "$1" in
  list_users) exit 0 ;;
  list_permissions) exit 0 ;;
  list_vhosts) printf '/\n' ;;
  set_permissions)
    printf '%s\t%s\t%s\t%s\n' "$4" "$5" "$6" "$7" >> "$BARKCLOUD_FAKE_RABBIT_LOG"
    ;;
esac
exit 0
SH
chmod +x "$temp_root/bin/docker" "$temp_root/bin/rabbitmqctl"

export BARKCLOUD_FAKE_DOCKER_LOG="$temp_root/docker.log"
export BARKCLOUD_FAKE_RABBIT_LOG="$temp_root/permissions.tsv"
export RABBITMQ_USERS_PASSWORD=test-users
export RABBITMQ_IDENTITY_PASSWORD=test-identity
export RABBITMQ_FILES_PASSWORD=test-files
export RABBITMQ_TORRENT_PASSWORD=test-torrent
export RABBITMQ_NOTIFICATION_PASSWORD=test-notification
export RABBITMQ_VIRTUAL_HOST=/

PATH="$temp_root/bin:$PATH" bash "$repo_root/Backend/provision-rabbitmq.sh" >/dev/null

[[ $(wc -l < "$temp_root/permissions.tsv") -eq 5 ]] || fail "expected five isolated RabbitMQ users"
grep -F $'barkcloud_users\t' "$temp_root/permissions.tsv" | grep -Fq 'UserDeleted' || fail "Users publish permissions omit UserDeleted"
identity_row=$(grep -F $'barkcloud_identity\t' "$temp_root/permissions.tsv")
identity_write=$(cut -f3 <<< "$identity_row")
identity_read=$(cut -f4 <<< "$identity_row")
[[ "$identity_write" == *'EmailNotification'* ]] || fail "Identity email publish permission is missing"
[[ "$identity_read" == *'UserDeleted'* ]] || fail "Identity user-deleted event consumption permission is missing"
grep -F $'barkcloud_files\t' "$temp_root/permissions.tsv" | grep -Fq 'files-upload-scheduler' || fail "Files Quartz queue permission is missing"
grep -F $'barkcloud_files\t' "$temp_root/permissions.tsv" | grep -Fq 'ScheduleMessage' || fail "Files Quartz schedule command permission is missing"
grep -F $'barkcloud_notification\t' "$temp_root/permissions.tsv" | grep -Fq 'notifications-email-handler(_error|_skipped)?' || fail "Notification error/skipped queue permissions are missing"
notification_read=$(grep -F $'barkcloud_notification\t' "$temp_root/permissions.tsv" | cut -f4)
[[ "$notification_read" == *'EmailNotification'* && "$notification_read" == *'notifications-email-handler'* ]] || fail "Notification email consume permissions are missing"

# Exercise the permission expressions, including the extra contracts registered by AddQuartzConsumers.
files_row=$(grep -F $'barkcloud_files\t' "$temp_root/permissions.tsv")
files_configure=$(cut -f2 <<< "$files_row")
files_write=$(cut -f3 <<< "$files_row")
files_read=$(cut -f4 <<< "$files_row")
for command in ScheduleMessage ScheduleRecurringMessage CancelScheduledMessage CancelScheduledRecurringMessage \
               PauseScheduledRecurringMessage ResumeScheduledRecurringMessage; do
  exchange="MassTransit.Scheduling:$command"
  fault="MassTransit:Fault--$exchange--"
  for pattern in "$files_configure" "$files_write" "$files_read"; do
    [[ "$exchange" =~ $pattern ]] || fail "Files cannot configure/bind scheduler command $command"
    [[ "$fault" =~ $pattern ]] || fail "Files cannot publish fault for scheduler command $command"
  done
done
for service in barkcloud_users barkcloud_files barkcloud_torrent; do
  row=$(grep -F "$service"$'\t' "$temp_root/permissions.tsv")
  read_pattern=$(cut -f4 <<< "$row")
  write_pattern=$(cut -f3 <<< "$row")
  for resource in 'BarkCloud.Shared.Queue.Notifications:EmailNotification' \
                  'BarkCloud.Shared.Queue.Notifications:Notification' \
                  'MassTransit:Fault--BarkCloud.Shared.Queue.Notifications:EmailNotification--' \
                  notifications-email-handler notifications-email-handler_error notifications-email-handler_skipped; do
    [[ ! "$resource" =~ $read_pattern ]] || fail "$service can bind or read protected email resource $resource"
    [[ ! "$resource" =~ $write_pattern ]] || fail "$service can publish to protected email resource $resource"
  done
  [[ ! 'MassTransit:Fault' =~ $read_pattern ]] || fail "$service can bind to the shared fault exchange"
done

for service in barkcloud_files barkcloud_torrent barkcloud_users; do
  row=$(grep -F "$service"$'\t' "$temp_root/permissions.tsv")
  configure=${row#*$'\t'}
  configure=${configure%%$'\t'*}
  write=$(cut -f3 <<< "$row")
  read_pattern=${row##*$'\t'}
  [[ "$configure$write$read_pattern" != *'EmailNotification'* \
     && "$configure$write$read_pattern" != *'Notification:Notification'* \
     && "$configure$write$read_pattern" != *'notifications-email-handler'* ]] \
    || fail "$service can read an email route"
done
for service in barkcloud_files barkcloud_torrent barkcloud_users; do
  row=$(grep -F "$service"$'\t' "$temp_root/permissions.tsv")
  [[ "$row" != *'Fault--BarkCloud\.Shared\.Queue\.Notifications:EmailNotification--'* ]] \
    || fail "$service can access Fault<EmailNotification>"
  [[ "$row" != *'MassTransit:Fault.*'* ]] || fail "$service has broad Fault permissions"
done
! grep -Fq '.*' "$repo_root/Backend/provision-rabbitmq.sh" || fail "RabbitMQ permissions contain a broad wildcard"

: > "$BARKCLOUD_FAKE_DOCKER_LOG"
PATH="$temp_root/bin:$PATH" bash "$repo_root/Backend/provision-rabbitmq.sh" --external >/dev/null
[[ ! -s "$BARKCLOUD_FAKE_DOCKER_LOG" ]] || fail "external RabbitMQ provisioning incorrectly selects local Compose broker"

deployment="$temp_root/deployment"
mkdir -p "$deployment"
cp "$repo_root/Backend/provision-isolation.sh" "$repo_root/Backend/provision-postgres.sql" \
  "$repo_root/Backend/provision-postgres-ownership.sql" "$repo_root/Backend/provision-rabbitmq.sh" "$deployment/"
cat > "$deployment/.env" <<'ENV'
POSTGRES_USER=operator
POSTGRES_PASSWORD=fake-operator-password
CONFIGURATION_DB_PASSWORD=fake-config-db
IDENTITY_DB_PASSWORD=fake-identity-db
USERS_DB_PASSWORD=fake-users-db
FILES_DB_PASSWORD=fake-files-db
TORRENT_DB_PASSWORD=fake-torrent-db
RABBITMQ_USERS_PASSWORD=fake-users-rabbit
RABBITMQ_IDENTITY_PASSWORD=fake-identity-rabbit
RABBITMQ_FILES_PASSWORD=fake-files-rabbit
RABBITMQ_TORRENT_PASSWORD=fake-torrent-rabbit
RABBITMQ_NOTIFICATION_PASSWORD=fake-notification-rabbit
RABBITMQ_VIRTUAL_HOST=/
ENV
: > "$BARKCLOUD_FAKE_DOCKER_LOG"
: > "$BARKCLOUD_FAKE_RABBIT_LOG"
BARKCLOUD_APPS_STOPPED=YES BARKCLOUD_FAKE_DOCKER_COMPOSE=YES \
  PATH="$temp_root/bin:$PATH" bash "$deployment/provision-isolation.sh" fresh --rabbitmq-external >/dev/null \
  || fail "external RabbitMQ orchestration fixture failed"
grep -Fq 'cloud-postgres' "$BARKCLOUD_FAKE_DOCKER_LOG" || fail "orchestrator skipped local PostgreSQL provisioning"
! grep -Fq 'cloud-rabbitmq' "$BARKCLOUD_FAKE_DOCKER_LOG" || fail "orchestrator selected local RabbitMQ for an external broker"
[[ $(wc -l < "$BARKCLOUD_FAKE_RABBIT_LOG") -eq 5 ]] || fail "orchestrator did not apply all RabbitMQ service permissions"

postgres_sql="$repo_root/Backend/provision-postgres.sql"
ownership_sql="$repo_root/Backend/provision-postgres-ownership.sql"
grep -Fq 'NOCREATEDB NOCREATEROLE NOBYPASSRLS' "$postgres_sql" || fail "PostgreSQL roles are missing least-privilege flags"
grep -Fq 'REVOKE CONNECT, TEMPORARY ON DATABASE configuration FROM PUBLIC' "$postgres_sql" || fail "PUBLIC connect is not revoked on app databases"
grep -Fq 'files_quartz' "$postgres_sql" || fail "Files Quartz schema is not provisioned"
grep -Fq "dependency.deptype = 'e'" "$ownership_sql" || fail "extension-owned PostgreSQL objects are not excluded"
! grep -Fq 'REASSIGN OWNED' "$repo_root/Backend"/*.sql || fail "broad REASSIGN OWNED is forbidden"

echo "PASS: provisioning script syntax, exact RabbitMQ permissions, and schema-scoped PostgreSQL transfer checks."
