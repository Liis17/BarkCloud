#!/usr/bin/env bash
# Переход на разделённые секреты (аудит безопасности, п. 9): RSA-ключи JWT, bootstrap-ключи
# сервисов, секреты сервисных токенов и ключ админ-cookie. Идемпотентна: существующие значения
# не перезаписывает, .env бэкапит. Базы данных НЕ трогает — старые секреты из БД Configuration
# удалит EF-миграция RemoveLegacySharedSecrets при следующем старте Configuration.
#
# Использование: cd Backend && ./migrate-secrets.sh
set -euo pipefail

ENV_FILE=".env"
SECRETS_DIR="secrets/jwt"

[[ -f "$ENV_FILE" ]] || { echo "Нет $ENV_FILE — запустите из папки с docker-compose.yml"; exit 1; }

cp "$ENV_FILE" "$ENV_FILE.bak.$(date +%Y%m%d%H%M%S)"
echo "Бэкап: $ENV_FILE.bak.*"

gen_secret() { openssl rand -base64 36 | tr -d '=+/' | cut -c1-43; }

# Ключи .env, отсутствующие или с placeholder-значением, заполняем; существующие не трогаем.
ensure_env() {
  local key="$1" value="$2"
  local current
  current=$(grep -E "^${key}=" "$ENV_FILE" | tail -n1 | cut -d= -f2- || true)
  if [[ -z "$current" || "$current" == replace-* ]]; then
    if grep -qE "^${key}=" "$ENV_FILE"; then
      sed -i -E "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
    else
      echo "${key}=${value}" >> "$ENV_FILE"
    fi
    echo "  + ${key}"
  fi
}

# --- RSA-пара JWT (RS256): приватный ключ только у Identity ---
mkdir -p "$SECRETS_DIR"
if [[ ! -f "$SECRETS_DIR/jwt-private.pem" ]]; then
  umask 077
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$SECRETS_DIR/jwt-private.pem"
  echo "  + $SECRETS_DIR/jwt-private.pem (RSA-3072)"
fi
if [[ ! -f "$SECRETS_DIR/jwt-public.pem" ]]; then
  openssl pkey -in "$SECRETS_DIR/jwt-private.pem" -pubout -out "$SECRETS_DIR/jwt-public.pem"
  chmod 644 "$SECRETS_DIR/jwt-public.pem"
  echo "  + $SECRETS_DIR/jwt-public.pem"
fi

# --- Bootstrap-ключи Configuration и секреты сервисных токенов ---
declare -A BOOTSTRAP CREDENTIAL
for svc in Identity Users Notification Files Torrent Web; do
  BOOTSTRAP[$svc]=$(gen_secret)
done
for svc in Identity Users Files Torrent Web; do
  CREDENTIAL[$svc]=$(gen_secret)
done

registry=""
for svc in Identity Users Notification Files Torrent Web; do
  ensure_env "${svc^^}_CONFIG_ACCESS_KEY" "${BOOTSTRAP[$svc]}"
  registry+="${svc}=${BOOTSTRAP[$svc]};"
done
ensure_env "CONFIGURATION_ACCESS_KEYS" "${registry%;}"

svc_registry=""
for svc in Identity Users Files Torrent Web; do
  ensure_env "${svc^^}_SERVICE_CREDENTIAL" "${CREDENTIAL[$svc]}"
  svc_registry+="${svc}=${CREDENTIAL[$svc]};"
done
ensure_env "SERVICE_CREDENTIALS" "${svc_registry%;}"

ensure_env "WEB_ADMIN_COOKIE_KEY" "$(gen_secret)$(gen_secret)"

# --- Убираем упразднённый общий ключ, если он ещё в .env ---
sed -i -E '/^CONFIGURATION_ACCESS_KEY=/d' "$ENV_FILE" || true

echo "Готово. Порядок перехода (окно обслуживания):"
echo "  1) docker compose down (завершить обработку сообщений)"
echo "  2) openssl-ключи и .env обновлены этим скриптом — проверьте их в репозитории деплоя"
echo "  3) docker compose up -d cloud-configuration (миграция чистит JwtSettings:SecretKey и *Service:Token)"
echo "  4) docker compose up -d (остальные сервисы; старые refresh-сессии недействительны —"
echo "     пользователи входят заново)"
