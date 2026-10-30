#!/usr/bin/env bash
set -euo pipefail

external=${1:-}
if [[ -n "$external" && "$external" != --external ]]; then
  echo "Использование: bash ./provision-rabbitmq.sh [--external]" >&2
  exit 2
fi

if [[ "$external" != --external ]] && command -v docker >/dev/null 2>&1 \
   && docker compose config --services 2>/dev/null | grep -qx 'cloud-rabbitmq'; then
  rabbitmqctl() { docker compose exec -T cloud-rabbitmq rabbitmqctl "$@"; }
elif command -v rabbitmqctl >/dev/null 2>&1; then
  rabbitmqctl() { command rabbitmqctl "$@"; }
else
  echo "RabbitMQ CLI is unavailable. Include cloud-rabbitmq in Compose or run this script in the broker node shell." >&2
  exit 1
fi

vhost=${RABBITMQ_VIRTUAL_HOST:-/}
if ! rabbitmqctl list_vhosts -q | awk -v vhost="$vhost" '$0 == vhost { found=1 } END { exit !found }'; then
  echo "RabbitMQ vhost '$vhost' does not exist. Create it with the operator account before provisioning." >&2
  exit 1
fi

configure_user() {
  local user="$1" password="$2" configure="$3" write="$4" read="$5" other_vhost

  if rabbitmqctl list_users -q | awk -v user="$user" '$1 == user { found=1 } END { exit !found }'; then
    rabbitmqctl change_password "$user" "$password" >/dev/null
  else
    rabbitmqctl add_user "$user" "$password" >/dev/null
  fi
  rabbitmqctl set_user_tags "$user" >/dev/null

  while IFS= read -r other_vhost; do
    [[ -n "$other_vhost" && "$other_vhost" != "$vhost" ]] || continue
    if rabbitmqctl list_permissions -p "$other_vhost" -q \
        | awk -v user="$user" '$1 == user { found=1 } END { exit !found }'; then
      rabbitmqctl clear_permissions -p "$other_vhost" "$user" >/dev/null
    fi
  done < <(rabbitmqctl list_vhosts -q)

  rabbitmqctl set_permissions -p "$vhost" "$user" "$configure" "$write" "$read" >/dev/null
}

users_bus='bark-users-bus-[0-9a-f]{32}(_error|_skipped)?'
identity_bus='bark-identity-bus-[0-9a-f]{32}(_error|_skipped)?'
files_bus='bark-files-bus-[0-9a-f]{32}(_error|_skipped)?'
torrent_bus='bark-torrent-bus-[0-9a-f]{32}(_error|_skipped)?'
notification_bus='bark-notification-bus-[0-9a-f]{32}(_error|_skipped)?'

user_events='BarkCloud\.Shared\.Queue\.Users:(UserChangedName|UserChangedUsername|UserChangedAvatar|UserChangedBio|UserDeleted)'
deleted_user='BarkCloud\.Shared\.Queue\.Users:UserDeleted'
process_file='BarkCloud\.Shared\.Queue\.Files:ProcessUploadedFile'
scheduled_types='ScheduleMessage|ScheduleRecurringMessage|CancelScheduledMessage|CancelScheduledRecurringMessage|PauseScheduledRecurringMessage|ResumeScheduledRecurringMessage'
scheduled_messages="MassTransit\\.Scheduling:(${scheduled_types})"
scheduled_faults="MassTransit:Fault--MassTransit\\.Scheduling:(${scheduled_types})--"
email_messages='BarkCloud\.Shared\.Queue\.Notifications:EmailNotification'
notification_base='BarkCloud\.Shared\.Queue\.Notifications:Notification'
fault_base='MassTransit:Fault'
user_deleted_fault='MassTransit:Fault--BarkCloud\.Shared\.Queue\.Users:UserDeleted--'
process_file_fault='MassTransit:Fault--BarkCloud\.Shared\.Queue\.Files:ProcessUploadedFile--'
email_fault='MassTransit:Fault--BarkCloud\.Shared\.Queue\.Notifications:EmailNotification--'

configure_user barkcloud_users "$RABBITMQ_USERS_PASSWORD" \
  "^(${users_bus}|${user_events})$" \
  "^(${users_bus}|${user_events})$" \
  "^(${users_bus})$"

configure_user barkcloud_identity "$RABBITMQ_IDENTITY_PASSWORD" \
  "^(${identity_bus}|user-deleted-identity(_error|_skipped)?|${deleted_user}|${email_messages}|${notification_base}|${fault_base}|${user_deleted_fault})$" \
  "^(${identity_bus}|user-deleted-identity(_error|_skipped)?|${email_messages}|${notification_base}|${fault_base}|${user_deleted_fault})$" \
  "^(${identity_bus}|user-deleted-identity|${deleted_user}|${email_messages}|${user_deleted_fault})$"

configure_user barkcloud_files "$RABBITMQ_FILES_PASSWORD" \
  "^(${files_bus}|user-deleted-files(_error|_skipped)?|process-uploaded-file(_error|_skipped)?|files-upload-scheduler(_error|_skipped)?|${deleted_user}|${process_file}|${scheduled_messages}|${scheduled_faults}|${fault_base}|${user_deleted_fault}|${process_file_fault})$" \
  "^(${files_bus}|user-deleted-files(_error|_skipped)?|process-uploaded-file(_error|_skipped)?|files-upload-scheduler(_error|_skipped)?|${process_file}|${scheduled_messages}|${scheduled_faults}|${fault_base}|${user_deleted_fault}|${process_file_fault})$" \
  "^(${files_bus}|user-deleted-files|process-uploaded-file|files-upload-scheduler|${deleted_user}|${process_file}|${scheduled_messages}|${scheduled_faults}|${user_deleted_fault}|${process_file_fault})$"

configure_user barkcloud_torrent "$RABBITMQ_TORRENT_PASSWORD" \
  "^(${torrent_bus}|user-deleted-torrent(_error|_skipped)?|${deleted_user}|${fault_base}|${user_deleted_fault})$" \
  "^(${torrent_bus}|user-deleted-torrent(_error|_skipped)?|${fault_base}|${user_deleted_fault})$" \
  "^(${torrent_bus}|user-deleted-torrent|${deleted_user}|${user_deleted_fault})$"

configure_user barkcloud_notification "$RABBITMQ_NOTIFICATION_PASSWORD" \
  "^(${notification_bus}|notifications-email-handler(_error|_skipped)?|${email_messages}|${notification_base}|${fault_base}|${email_fault})$" \
  "^(${notification_bus}|notifications-email-handler(_error|_skipped)?|${fault_base}|${email_fault})$" \
  "^(${notification_bus}|notifications-email-handler|${email_messages}|${email_fault})$"

echo "RabbitMQ service users and per-service permissions applied to vhost '$vhost'."
