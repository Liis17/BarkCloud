#!/usr/bin/env bash
set -euo pipefail

test_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$test_dir/../.." && pwd)
migration_script="$repo_root/Backend/migrate-secrets.sh"
sample_env="$repo_root/Backend/sample.env"
temp_root=$(mktemp -d "${TMPDIR:-/tmp}/barkcloud-migrate-secrets.XXXXXX")

cleanup() {
  case "$temp_root" in
    "${TMPDIR:-/tmp}"/barkcloud-migrate-secrets.*) rm -rf -- "$temp_root" ;;
    *) echo "Unexpected test directory; not removing it." >&2; return 1 ;;
  esac
}
trap cleanup EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

read_env_value() {
  local file="$1" key="$2" line value=""
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    if [[ "$line" == "$key="* ]]; then
      value=${line#*=}
    fi
  done < "$file"
  if [[ "$value" == "'"* ]]; then
    value=${value#\'}
    value=${value%%\'*}
  elif [[ "$value" == '"'* ]]; then
    value=${value#\"}
    value=${value%%\"*}
  fi
  printf '%s' "$value"
}

replace_env_line() {
  local file="$1" key="$2" value="$3" line found=0
  local temporary="$file.fixture"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    if [[ "$line" == "$key="* ]]; then
      printf '%s=%s\r\n' "$key" "$value" >> "$temporary"
      found=1
    else
      printf '%s\r\n' "$line" >> "$temporary"
    fi
  done < "$file"
  [[ "$found" -eq 1 ]] || fail "fixture key $key was not found"
  mv -- "$temporary" "$file"
}

assert_equal() {
  [[ "$1" == "$2" ]] || fail "$3"
}

assert_registry_matches() {
  local file="$1" registry_key="$2" suffix="$3" services="$4"
  local expected="" service value
  for service in $services; do
    value=$(read_env_value "$file" "${service^^}${suffix}")
    [[ -n "$value" && "$value" != replace-* ]] || fail "$registry_key contains an empty or placeholder secret"
    [[ "$value" != *';'* ]] || fail "${service^^}${suffix} contains the registry separator"
    [[ -z "$expected" ]] || expected+=';'
    expected+="$service=$value"
  done
  assert_equal "$(read_env_value "$file" "$registry_key")" "$expected" "$registry_key does not match its individual values"
}

make_fixture() {
  local directory="$1" line
  mkdir -p -- "$directory"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    printf '%s\r\n' "$line"
  done < "$sample_env" > "$directory/.env"
}

run_migration() {
  local directory="$1" output_file="$2"
  (cd -- "$directory" && bash "$migration_script") > "$output_file" 2>&1
}

sample_dir="$temp_root/sample"
make_fixture "$sample_dir"
run_migration "$sample_dir" "$temp_root/sample-first.log" || fail "sample.env migration failed"
sample_env_file="$sample_dir/.env"
assert_registry_matches "$sample_env_file" CONFIGURATION_ACCESS_KEYS _CONFIG_ACCESS_KEY "Identity Users Notification Files Torrent Web"
assert_registry_matches "$sample_env_file" SERVICE_CREDENTIALS _SERVICE_CREDENTIAL "Identity Users Files Torrent Web"
sample_snapshot="$(read_env_value "$sample_env_file" IDENTITY_CONFIG_ACCESS_KEY)|$(read_env_value "$sample_env_file" WEB_SERVICE_CREDENTIAL)|$(read_env_value "$sample_env_file" WEB_ADMIN_COOKIE_KEY)|$(read_env_value "$sample_env_file" CONFIGURATION_ACCESS_KEYS)|$(read_env_value "$sample_env_file" SERVICE_CREDENTIALS)"
run_migration "$sample_dir" "$temp_root/sample-repeat.log" || fail "sample.env repeat failed"
assert_equal "$(read_env_value "$sample_env_file" IDENTITY_CONFIG_ACCESS_KEY)|$(read_env_value "$sample_env_file" WEB_SERVICE_CREDENTIAL)|$(read_env_value "$sample_env_file" WEB_ADMIN_COOKIE_KEY)|$(read_env_value "$sample_env_file" CONFIGURATION_ACCESS_KEYS)|$(read_env_value "$sample_env_file" SERVICE_CREDENTIALS)" "$sample_snapshot" "sample.env repeat changed generated secrets"

partial_dir="$temp_root/partial"
make_fixture "$partial_dir"
partial_env_file="$partial_dir/.env"
replace_env_line "$partial_env_file" CONFIGURATION_ACCESS_KEYS "'Identity=legacy-identity-key;Users=registry-users-key;Notification=notification-key;Files=files-key;Torrent=torrent-key;Web=web-key'"
replace_env_line "$partial_env_file" SERVICE_CREDENTIALS "'Identity=identity-credential;Users=registry-users-credential;Files=files-credential;Torrent=torrent-credential;Web=web-credential'"
replace_env_line "$partial_env_file" IDENTITY_CONFIG_ACCESS_KEY "'known-identity-key'"
replace_env_line "$partial_env_file" USERS_CONFIG_ACCESS_KEY '"known-users-key" # operator value'
replace_env_line "$partial_env_file" USERS_SERVICE_CREDENTIAL "'known-users-credential=with=equals'"
run_migration "$partial_dir" "$temp_root/partial-first.log" || fail "quoted CRLF fixture migration failed"
assert_equal "$(read_env_value "$partial_env_file" IDENTITY_CONFIG_ACCESS_KEY)" "known-identity-key" "single-quoted custom key was not preserved"
assert_equal "$(read_env_value "$partial_env_file" USERS_CONFIG_ACCESS_KEY)" "known-users-key" "double-quoted custom key was not preserved"
assert_equal "$(read_env_value "$partial_env_file" IDENTITY_SERVICE_CREDENTIAL)" "identity-credential" "missing service credential was not restored from its registry"
assert_equal "$(read_env_value "$partial_env_file" USERS_SERVICE_CREDENTIAL)" "known-users-credential=with=equals" "single-quoted custom credential was not preserved"
assert_registry_matches "$partial_env_file" CONFIGURATION_ACCESS_KEYS _CONFIG_ACCESS_KEY "Identity Users Notification Files Torrent Web"
assert_registry_matches "$partial_env_file" SERVICE_CREDENTIALS _SERVICE_CREDENTIAL "Identity Users Files Torrent Web"
partial_snapshot="$(read_env_value "$partial_env_file" IDENTITY_CONFIG_ACCESS_KEY)|$(read_env_value "$partial_env_file" USERS_CONFIG_ACCESS_KEY)|$(read_env_value "$partial_env_file" USERS_SERVICE_CREDENTIAL)|$(read_env_value "$partial_env_file" CONFIGURATION_ACCESS_KEYS)|$(read_env_value "$partial_env_file" SERVICE_CREDENTIALS)"
replace_env_line "$partial_env_file" NOTIFICATION_CONFIG_ACCESS_KEY replace-notification-key
replace_env_line "$partial_env_file" FILES_SERVICE_CREDENTIAL replace-files-credential
run_migration "$partial_dir" "$temp_root/partial-repair.log" || fail "partial fixture repair failed"
assert_equal "$(read_env_value "$partial_env_file" IDENTITY_CONFIG_ACCESS_KEY)|$(read_env_value "$partial_env_file" USERS_CONFIG_ACCESS_KEY)|$(read_env_value "$partial_env_file" USERS_SERVICE_CREDENTIAL)|$(read_env_value "$partial_env_file" CONFIGURATION_ACCESS_KEYS)|$(read_env_value "$partial_env_file" SERVICE_CREDENTIALS)" "$partial_snapshot" "partial rerun did not recover values from registries"
assert_equal "$(read_env_value "$partial_env_file" NOTIFICATION_CONFIG_ACCESS_KEY)" "notification-key" "partial rerun did not recover the missing key from its registry"
assert_registry_matches "$partial_env_file" CONFIGURATION_ACCESS_KEYS _CONFIG_ACCESS_KEY "Identity Users Notification Files Torrent Web"
assert_registry_matches "$partial_env_file" SERVICE_CREDENTIALS _SERVICE_CREDENTIAL "Identity Users Files Torrent Web"

malformed_dir="$temp_root/malformed"
make_fixture "$malformed_dir"
replace_env_line "$malformed_dir/.env" IDENTITY_CONFIG_ACCESS_KEY '"unterminated'
if run_migration "$malformed_dir" "$temp_root/malformed.log"; then
  fail "malformed quoted secret was accepted"
fi
grep -q 'Незакрытая двойная кавычка в IDENTITY_CONFIG_ACCESS_KEY' "$temp_root/malformed.log" || fail "malformed quote did not report an actionable error"

padded_dir="$temp_root/padded"
make_fixture "$padded_dir"
replace_env_line "$padded_dir/.env" IDENTITY_CONFIG_ACCESS_KEY "' padded-key '"
if run_migration "$padded_dir" "$temp_root/padded.log"; then
  fail "secret padding that the runtime registry trims was accepted"
fi
grep -q 'крайние пробелы' "$temp_root/padded.log" || fail "secret padding did not report an actionable error"

echo "PASS: sample generation/rerun, CRLF quoted custom values, partial registry recovery, and malformed-value rejection."
