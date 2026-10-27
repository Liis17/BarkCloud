#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd "$SCRIPT_DIR"
[[ -f .env ]] || { echo "Нет .env рядом с provisioning-скриптами." >&2; exit 1; }

mode=${1:-}
postgres_external=0
rabbitmq_external=0
for option in "${@:2}"; do
  case "$option" in
    --external) postgres_external=1 ;;
    --rabbitmq-external) rabbitmq_external=1 ;;
    *) echo "Неизвестный параметр: $option" >&2; exit 2 ;;
  esac
done
if [[ "$mode" != fresh && "$mode" != existing ]]; then
  echo "Использование: bash ./provision-isolation.sh fresh|existing [--external] [--rabbitmq-external]" >&2
  exit 2
fi

read_env_value() {
  local key="$1" line raw value="" suffix char next found=0 first=1 i closed=0
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    if [[ "$first" -eq 1 ]]; then
      line=${line#$'\xef\xbb\xbf'}
      first=0
    fi
    if [[ "$line" =~ ^[[:space:]]*${key}[[:space:]]*=[[:space:]]*(.*)$ ]]; then
      [[ "$found" -eq 0 ]] || { echo "Повторная запись $key в .env." >&2; return 1; }
      raw=${BASH_REMATCH[1]}
      raw="${raw#"${raw%%[![:space:]]*}"}"
      if [[ "${raw:0:1}" == "'" ]]; then
        for ((i = 1; i < ${#raw}; i++)); do
          char=${raw:i:1}
          if [[ "$char" == $'\\' && "${raw:$((i + 1)):1}" == "'" ]]; then
            value+="'"
            i=$((i + 1))
          elif [[ "$char" == "'" ]]; then
            suffix=${raw:$((i + 1))}
            closed=1
            break
          else
            value+="$char"
          fi
        done
        if [[ "$closed" -ne 1 ]]; then
          echo "Незакрытая кавычка в $key (.env)." >&2
          return 1
        fi
        suffix="${suffix%"${suffix##*[![:space:]]}"}"
        [[ -z "$suffix" || "$suffix" =~ ^[[:space:]]+# ]] || {
          echo "Неподдерживаемый текст после кавычки в $key (.env)." >&2
          return 1
        }
      elif [[ "${raw:0:1}" == '"' ]]; then
        for ((i = 1; i < ${#raw}; i++)); do
          char=${raw:i:1}
          if [[ "$char" == $'\\' ]]; then
            i=$((i + 1))
            (( i < ${#raw} )) || { echo "Незаконченная escape в $key (.env)." >&2; return 1; }
            next=${raw:i:1}
            case "$next" in
              '"'|$'\\') value+="$next" ;;
              *) echo "Неподдерживаемая escape в $key (.env)." >&2; return 1 ;;
            esac
          elif [[ "$char" == '"' ]]; then
            suffix=${raw:$((i + 1))}
            closed=1
            break
          else
            value+="$char"
          fi
        done
        if [[ "$closed" -ne 1 || "$value" == *'$'* ]]; then
          echo "Некорректное quoted-значение $key (.env); используйте одинарные кавычки для literal-секретов." >&2
          return 1
        fi
        suffix="${suffix%"${suffix##*[![:space:]]}"}"
        [[ -z "$suffix" || "$suffix" =~ ^[[:space:]]+# ]] || {
          echo "Неподдерживаемый текст после кавычки в $key (.env)." >&2
          return 1
        }
      else
        value="$raw"
        if [[ "$value" == *' #'* ]]; then value=${value%%' #'*}; fi
        value="${value%"${value##*[![:space:]]}"}"
        [[ "$value" != *'$'* && "$value" != *$'\n'* ]] || {
          echo "Значение $key должно быть literal; используйте одинарные кавычки в .env." >&2
          return 1
        }
      fi
      found=1
    elif [[ "$line" =~ ^[[:space:]]*export[[:space:]]+${key}([[:space:]]|=) ]]; then
      echo "Формат export для $key не поддерживается в Docker Compose .env." >&2
      return 1
    fi
  done < .env
  [[ "$found" -eq 1 ]] || return 0
  printf '%s' "$value"
}

load_required() {
  local key="$1" value=${!1:-}
  if [[ -z "$value" ]]; then value=$(read_env_value "$key"); fi
  if [[ -z "$value" || "$value" == replace-* ]]; then
    echo "$key не задан или содержит placeholder; заполните .env или экспортируйте его перед запуском." >&2
    exit 1
  fi
  export "$key=$value"
}

for key in POSTGRES_USER POSTGRES_PASSWORD CONFIGURATION_DB_PASSWORD IDENTITY_DB_PASSWORD \
           USERS_DB_PASSWORD FILES_DB_PASSWORD TORRENT_DB_PASSWORD RABBITMQ_USERS_PASSWORD \
           RABBITMQ_IDENTITY_PASSWORD RABBITMQ_FILES_PASSWORD RABBITMQ_TORRENT_PASSWORD \
           RABBITMQ_NOTIFICATION_PASSWORD; do
  load_required "$key"
done
RABBITMQ_VIRTUAL_HOST=${RABBITMQ_VIRTUAL_HOST:-$(read_env_value RABBITMQ_VIRTUAL_HOST)}
RABBITMQ_VIRTUAL_HOST=${RABBITMQ_VIRTUAL_HOST:-/}
export RABBITMQ_VIRTUAL_HOST

if [[ "$mode" == existing ]]; then
  [[ "${BARKCLOUD_BACKUP_VERIFIED:-}" == YES ]] || {
    echo "Сначала создайте и проверьте резервные копии PostgreSQL и RabbitMQ, затем установите BARKCLOUD_BACKUP_VERIFIED=YES." >&2
    exit 1
  }
  [[ "${BARKCLOUD_APPS_STOPPED:-}" == YES ]] || {
    echo "Остановите все приложения и установите BARKCLOUD_APPS_STOPPED=YES." >&2
    exit 1
  }
fi

if [[ "$postgres_external" -eq 1 || "$rabbitmq_external" -eq 1 ]]; then
  if [[ "$mode" == fresh ]]; then
    [[ "${BARKCLOUD_APPS_STOPPED:-}" == YES ]] || {
      echo "Для внешней инфраструктуры подтвердите, что приложения остановлены: BARKCLOUD_APPS_STOPPED=YES." >&2
      exit 1
    }
  fi
else
  running_services=$(docker compose ps --services --status running) || {
    echo "Не удалось проверить Compose-сервисы. Запустите provisioning с доступным Docker daemon или используйте --external." >&2
    exit 1
  }
  for service in cloud-configuration cloud-identity cloud-users cloud-files cloud-notification cloud-torrent cloud-web; do
    if printf '%s\n' "$running_services" | grep -qx "$service"; then
      echo "Остановите приложение $service перед provisioning." >&2
      exit 1
    fi
  done
fi

postgres_args=(
  -X -v ON_ERROR_STOP=1 -v "mode=$mode"
  -v "configuration_password=$CONFIGURATION_DB_PASSWORD"
  -v "identity_password=$IDENTITY_DB_PASSWORD"
  -v "users_password=$USERS_DB_PASSWORD"
  -v "files_password=$FILES_DB_PASSWORD"
  -v "torrent_password=$TORRENT_DB_PASSWORD"
)

expanded_sql=$(mktemp)
trap 'rm -f "$expanded_sql"' EXIT
while IFS= read -r line || [[ -n "$line" ]]; do
  if [[ "$line" == '\ir provision-postgres-ownership.sql' ]]; then
    cat "$SCRIPT_DIR/provision-postgres-ownership.sql" >> "$expanded_sql"
  else
    printf '%s\n' "$line" >> "$expanded_sql"
  fi
done < "$SCRIPT_DIR/provision-postgres.sql"

compose_services=$(docker compose config --services 2>/dev/null || true)
if [[ "$postgres_external" -eq 0 ]] && printf '%s\n' "$compose_services" | grep -qx 'cloud-postgres'; then
  docker compose exec -T -e "PGPASSWORD=$POSTGRES_PASSWORD" cloud-postgres \
    psql "${postgres_args[@]}" -U "$POSTGRES_USER" -d postgres -f - < "$expanded_sql"
elif [[ "$postgres_external" -eq 1 && -n "${BARKCLOUD_POSTGRES_ADMIN_URL:-}" ]]; then
  PGPASSWORD="$POSTGRES_PASSWORD" psql "$BARKCLOUD_POSTGRES_ADMIN_URL" \
    "${postgres_args[@]}" -U "$POSTGRES_USER" -f "$expanded_sql"
else
  echo "PostgreSQL Compose service is absent; use --external and export BARKCLOUD_POSTGRES_ADMIN_URL for the external cluster." >&2
  exit 1
fi

rabbitmq_args=()
if [[ "$rabbitmq_external" -eq 1 ]]; then rabbitmq_args+=(--external); fi
bash "$SCRIPT_DIR/provision-rabbitmq.sh" "${rabbitmq_args[@]}"
echo "Service isolation provisioned. Configuration stores no PostgreSQL root or RabbitMQ administrator password."
