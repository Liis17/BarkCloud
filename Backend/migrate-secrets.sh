#!/usr/bin/env bash
# Подготавливает RSA-ключи JWT, bootstrap-ключи и пароли сервисных PostgreSQL/RabbitMQ-пользователей.
# Индивидуальные значения не меняет, если они заданы; реестры собирает из фактических значений.
# Инфраструктуру не меняет. Для создания/перехода ролей и ACL отдельно запустите
# provision-isolation.sh fresh (новая установка) или provision-isolation.sh existing.
#
# Использование из каталога с docker-compose.yml и .env:
# bash /path/to/BarkCloud/Backend/migrate-secrets.sh
set -euo pipefail

ENV_FILE=".env"
SECRETS_DIR="secrets/jwt"

[[ -f "$ENV_FILE" ]] || { echo "Нет $ENV_FILE — запустите из папки с docker-compose.yml"; exit 1; }

BACKUP_FILE="$ENV_FILE.bak.$(date +%Y%m%d%H%M%S).$$"
cp "$ENV_FILE" "$BACKUP_FILE"
echo "Бэкап: $BACKUP_FILE"

gen_secret() { openssl rand -base64 36 | tr -d '=+/' | cut -c1-43; }

env_value() {
  local key="$1" line raw result="" found=0 first=1
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    if [[ "$first" -eq 1 ]]; then
      line=${line#$'\xef\xbb\xbf'}
      first=0
    fi
    if [[ "$line" =~ ^[[:space:]]*${key}[[:space:]]*[:=][[:space:]]*(.*)$ ]]; then
      raw=${BASH_REMATCH[1]}
      result=$(decode_env_value "$key" "$raw") || return 1
      found=1
    elif [[ "$line" =~ ^[[:space:]]*export[[:space:]]+${key}([[:space:]]|=|:) ]]; then
      echo "Формат export для $key не поддерживается в Docker Compose .env; используйте KEY=value." >&2
      return 1
    elif [[ "$line" =~ ^[[:space:]]*${key}([[:space:]]|$) ]]; then
      echo "Некорректная строка для $key в $ENV_FILE; ожидается KEY=value или KEY: value." >&2
      return 1
    fi
  done < "$ENV_FILE"
  [[ "$found" -eq 1 ]] || return 0
  printf '%s' "$result"
}

decode_env_value() {
  local key="$1" raw="$2" value="" char next suffix closed=0 i
  raw="${raw#"${raw%%[![:space:]]*}"}"

  if [[ "${raw:0:1}" == '"' ]]; then
    for ((i = 1; i < ${#raw}; i++)); do
      char=${raw:i:1}
      if [[ "$char" == $'\\' ]]; then
        i=$((i + 1))
        if (( i >= ${#raw} )); then
          echo "Незавершённая escape-последовательность в $key; проверьте значение в $ENV_FILE." >&2
          return 1
        fi
        next=${raw:i:1}
        case "$next" in
          $'\\'|'"') value+="$next" ;;
          *)
            echo "Неподдерживаемая escape-последовательность в $key; используйте одинарные кавычки для literal-секрета." >&2
            return 1
            ;;
        esac
      elif [[ "$char" == '"' ]]; then
        suffix=${raw:$((i + 1))}
        closed=1
        break
      else
        value+="$char"
      fi
    done
    if [[ "$closed" -ne 1 ]]; then
      echo "Незакрытая двойная кавычка в $key; проверьте $ENV_FILE." >&2
      return 1
    fi
    suffix="${suffix%"${suffix##*[![:space:]]}"}"
    if [[ -n "$suffix" && ! "$suffix" =~ ^[[:space:]]+# ]]; then
      echo "Текст после закрывающей кавычки в $key не поддерживается; проверьте $ENV_FILE." >&2
      return 1
    fi
    if [[ "$value" == *'$'* ]]; then
      echo "Значение $key содержит знак доллара в двойных кавычках, где Compose выполняет подстановку; используйте одинарные кавычки." >&2
      return 1
    fi
  elif [[ "${raw:0:1}" == "'" ]]; then
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
      echo "Незакрытая одинарная кавычка в $key; многострочные значения здесь не поддерживаются." >&2
      return 1
    fi
    suffix="${suffix%"${suffix##*[![:space:]]}"}"
    if [[ -n "$suffix" && ! "$suffix" =~ ^[[:space:]]+# ]]; then
      echo "Текст после закрывающей кавычки в $key не поддерживается; проверьте $ENV_FILE." >&2
      return 1
    fi
  else
    for ((i = 0; i < ${#raw}; i++)); do
      char=${raw:i:1}
      if [[ "$char" == '#' && "$i" -gt 0 && "${raw:$((i - 1)):1}" =~ [[:space:]] ]]; then
        break
      fi
      value+="$char"
    done
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    if [[ "$value" == *'$'* ]]; then
      echo "Значение $key содержит знак доллара без literal-кавычек; используйте одинарные кавычки." >&2
      return 1
    fi
  fi

  if [[ "$key" == *_CONFIG_ACCESS_KEY || "$key" == *_SERVICE_CREDENTIAL ]]; then
    if [[ "$value" == *';'* || "$value" == *$'\n'* || "$value" == *$'\r'*
          || "$value" != "${value#"${value%%[![:space:]]*}"}"
          || "$value" != "${value%"${value##*[![:space:]]}"}" ]]; then
      echo "Значение $key содержит разделитель, перевод строки или крайние пробелы, которые нельзя сохранить в реестре сервисных секретов." >&2
      return 1
    fi
  fi
  printf '%s' "$value"
}

write_env() {
  local key="$1" value="$2" escaped
  escaped=${value//\\/\\\\}
  escaped=${escaped//&/\\&}
  escaped=${escaped//|/\\|}
  if grep -qE "^[[:space:]]*${key}[[:space:]]*[:=]" "$ENV_FILE"; then
    sed -i -E "s|^[[:space:]]*${key}[[:space:]]*[:=].*|${key}=${escaped}|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

quote_env_value() {
  local value="$1" encoded="" char i
  for ((i = 0; i < ${#value}; i++)); do
    char=${value:i:1}
    if [[ "$char" == "'" ]]; then
      encoded+="\\'"
    else
      encoded+="$char"
    fi
  done
  printf "'%s'" "$encoded"
}

registry_value() {
  local registry_key="$1" service="$2" registry entry name value="" found=0
  local -a entries
  registry=$(env_value "$registry_key")
  [[ -n "$registry" ]] || return 0
  IFS=';' read -r -a entries <<< "$registry"
  for entry in "${entries[@]}"; do
    [[ -n "$entry" ]] || continue
    if [[ "$entry" != *=* ]]; then
      echo "Некорректная запись в $registry_key; ожидалось имя=секрет." >&2
      return 1
    fi
    name=${entry%%=*}
    if [[ "$name" == "$service" ]]; then
      if [[ "$found" -eq 1 ]]; then
        echo "Повторная запись $service в $registry_key; исправьте реестр вручную." >&2
        return 1
      fi
      value=${entry#*=}
      found=1
    fi
  done
  if [[ "$found" -eq 1 && -n "$value" && "$value" != replace-* ]]; then
    printf '%s' "$value"
  fi
}

# Индивидуальные секреты .env — источник истины. Значения replace-* и пустые значения
# заполняются; пользовательские значения остаются без изменений.
ensure_env() {
  local key="$1" value="$2" current
  current=$(env_value "$key")
  if [[ -z "$current" || "$current" == replace-* ]]; then
    write_env "$key" "$value"
    echo "  + $key"
  fi
}

ensure_generated_env() {
  local key="$1" generated="$2" current
  current=$(env_value "$key")
  if [[ -z "$current" || "$current" == replace-* ]]; then
    write_env "$key" "$(quote_env_value "$generated")"
    echo "  + $key"
  fi
}

ensure_secret_env() {
  local key="$1" service="$2" registry_key="$3" generated="$4" current restored
  current=$(env_value "$key")
  if [[ -z "$current" || "$current" == replace-* ]]; then
    restored=$(registry_value "$registry_key" "$service")
    if [[ -n "$restored" ]]; then
      write_env "$key" "$(quote_env_value "$restored")"
      echo "  + $key (восстановлен из реестра)"
    else
      write_env "$key" "$generated"
      echo "  + $key"
    fi
  fi
}

# Реестры — производные значения, всегда приводятся к индивидуальным секретам выше.
sync_registry() {
  local key="$1" value="$2" current
  current=$(env_value "$key")
  if [[ "$current" != "$value" ]]; then
    write_env "$key" "$(quote_env_value "$value")"
    echo "  ~ $key (синхронизирован с индивидуальными секретами)"
  fi
}

secure_private_key() {
  local key_file="$1" owner mode
  [[ "$(uname -s)" == Linux* ]] || return 0

  owner=$(stat -c '%u' -- "$key_file")
  mode=$(stat -c '%a' -- "$key_file")
  if [[ "$owner" == "1654" ]]; then
    if [[ "$mode" != "600" ]] && ! chmod 600 -- "$key_file"; then
      echo "Не удалось установить режим 600 для $key_file. Выполните: sudo chmod 600 '$key_file'" >&2
      exit 1
    fi
    return 0
  fi

  if [[ "$EUID" -ne 0 && "$owner" != "$(id -u)" ]]; then
    echo "Приватный JWT-ключ принадлежит UID $owner; запустите скрипт от владельца файла или от root." >&2
    exit 1
  fi
  if command -v setfacl >/dev/null 2>&1; then
    if ! chmod 600 -- "$key_file" || ! setfacl -b -- "$key_file" || ! setfacl -m u:1654:r -- "$key_file"; then
      echo "Не удалось выдать POSIX ACL только UID 1654 для $key_file. Проверьте поддержку ACL файловой системой и наличие пакета acl." >&2
      echo "Если ACL недоступен, выполните: sudo chown 1654:1654 '$key_file' && sudo chmod 600 '$key_file', затем повторите запуск." >&2
      exit 1
    fi
  elif [[ "$EUID" -eq 0 ]]; then
    if ! chown 1654:1654 -- "$key_file" || ! chmod 600 -- "$key_file"; then
      echo "Не удалось передать $key_file пользователю контейнера UID 1654." >&2
      exit 1
    fi
  else
    echo "Для чтения ключа Identity (UID 1654) установите пакет acl и повторите запуск," >&2
    echo "либо передайте ключ контейнерному UID: sudo chown 1654:1654 '$key_file' && sudo chmod 600 '$key_file'." >&2
    exit 1
  fi
}

# --- RSA-пара JWT (RS256): приватный ключ только у Identity ---
mkdir -p "$SECRETS_DIR"
PRIVATE_KEY="$SECRETS_DIR/jwt-private.pem"
PUBLIC_KEY="$SECRETS_DIR/jwt-public.pem"
if [[ ! -f "$PRIVATE_KEY" && -f "$PUBLIC_KEY" ]]; then
  echo "Найден jwt-public.pem без jwt-private.pem; восстановите соответствующую пару, ключ не перегенерируется." >&2
  exit 1
fi
if [[ ! -f "$PRIVATE_KEY" ]]; then
  umask 077
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$PRIVATE_KEY" 2>/dev/null
  echo "  + $PRIVATE_KEY (RSA-3072)"
fi
secure_private_key "$PRIVATE_KEY"

if [[ ! -f "$PUBLIC_KEY" ]]; then
  if ! openssl pkey -in "$PRIVATE_KEY" -pubout -out "$PUBLIC_KEY" 2>/dev/null; then
    echo "Не удалось прочитать $PRIVATE_KEY для восстановления публичного ключа; запустите от владельца файла или root." >&2
    exit 1
  fi
  echo "  + $PUBLIC_KEY (восстановлен из существующего приватного ключа)"
fi

if [[ "$(uname -s)" == Linux* ]]; then
  public_mode=$(stat -c '%a' -- "$PUBLIC_KEY")
  [[ "$public_mode" == "644" ]] || chmod 644 -- "$PUBLIC_KEY"
else
  chmod 644 "$PUBLIC_KEY" 2>/dev/null || true
fi

if ! private_public=$(openssl pkey -in "$PRIVATE_KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n'); then
  echo "Не удалось прочитать приватный JWT-ключ; проверьте его PEM и права доступа." >&2
  exit 1
fi
if ! configured_public=$(openssl pkey -pubin -in "$PUBLIC_KEY" -outform DER 2>/dev/null | base64 | tr -d '\n'); then
  echo "Не удалось прочитать публичный JWT-ключ; проверьте его PEM." >&2
  exit 1
fi
if [[ "$private_public" != "$configured_public" ]]; then
  echo "jwt-private.pem и jwt-public.pem не образуют пару; восстановите согласованные ключи, файлы не перезаписаны." >&2
  exit 1
fi
unset private_public configured_public

# --- Bootstrap-ключи Configuration и секреты сервисных токенов ---
declare -A BOOTSTRAP CREDENTIAL
registry=""
for svc in Identity Users Notification Files Torrent Web; do
  env_key="${svc^^}_CONFIG_ACCESS_KEY"
  ensure_secret_env "$env_key" "$svc" "CONFIGURATION_ACCESS_KEYS" "$(gen_secret)"
  BOOTSTRAP[$svc]=$(env_value "$env_key")
  registry+="${svc}=${BOOTSTRAP[$svc]};"
done
sync_registry "CONFIGURATION_ACCESS_KEYS" "${registry%;}"

svc_registry=""
for svc in Identity Users Files Torrent Web; do
  env_key="${svc^^}_SERVICE_CREDENTIAL"
  ensure_secret_env "$env_key" "$svc" "SERVICE_CREDENTIALS" "$(gen_secret)"
  CREDENTIAL[$svc]=$(env_value "$env_key")
  svc_registry+="${svc}=${CREDENTIAL[$svc]};"
done
sync_registry "SERVICE_CREDENTIALS" "${svc_registry%;}"

ensure_env "WEB_ADMIN_COOKIE_KEY" "$(gen_secret)$(gen_secret)"

# Сервисные роли никогда не используют root-учётные данные инфраструктуры.
for key in CONFIGURATION_DB_PASSWORD IDENTITY_DB_PASSWORD USERS_DB_PASSWORD FILES_DB_PASSWORD TORRENT_DB_PASSWORD \
           RABBITMQ_IDENTITY_PASSWORD RABBITMQ_USERS_PASSWORD RABBITMQ_FILES_PASSWORD \
           RABBITMQ_TORRENT_PASSWORD RABBITMQ_NOTIFICATION_PASSWORD; do
  ensure_generated_env "$key" "$(gen_secret)"
done

ensure_generated_env "TRANSCODER_TOKEN" "$(gen_secret)"

# --- Убираем упразднённый общий ключ, если он ещё в .env ---
sed -i -E '/^CONFIGURATION_ACCESS_KEY=/d' "$ENV_FILE" || true

echo "Готово. Порядок перехода (окно обслуживания):"
echo "  1) Сделайте проверенные резервные копии PostgreSQL и RabbitMQ, затем остановите все приложения."
echo "  2) До любого перезапуска установите вместе обновлённые docker-compose.yml, .env и provisioning-скрипты; сохраните secrets/jwt/."
echo "  3) Для существующей установки выполните BARKCLOUD_BACKUP_VERIFIED=YES BARKCLOUD_APPS_STOPPED=YES bash ./provision-isolation.sh existing."
echo "  4) Запустите Configuration: миграция удалит устаревшие общие RabbitMQ credentials и root-credential connection strings вместе с их историей; новые значения заполняются из сервисных секретов."
echo "  5) После успешного старта Configuration запустите остальные новые backend-образы. Не смешивайте старые и новые образы."
echo "  Откат после шага 3 требует совместно восстановить старые образы, compose, .env, дамп PostgreSQL и definitions/резервную копию RabbitMQ."
echo "  Refresh-сессии Identity в этой миграции не удаляются; возможность их использования определяется действующими ключами и конфигурацией, поэтому проверьте сессию до планирования повторного входа."
echo "  Подробные шаги для свежей установки, внешней инфраструктуры и границы доверия Web: isolation-guide.md."
