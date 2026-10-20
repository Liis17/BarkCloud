#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
resolver="$repo_root/.github/actions/docker-version/resolve-version.sh"
temp_dir="$(mktemp -d)"
mock_bin="$temp_dir/bin"
mkdir -p "$mock_bin"
cleanup() {
  rm -f "$mock_bin/curl" \
    "$temp_dir/dev-max-version" \
    "$temp_dir/dev-repo-missing" \
    "$temp_dir/dev-ahead-of-master" \
    "$temp_dir/master-uses-dev-version" \
    "$temp_dir/master-without-dev-version" \
    "$temp_dir/master-without-dev-version.stderr" \
    "$temp_dir/first-dev-version"
  rmdir "$mock_bin" "$temp_dir"
}
trap cleanup EXIT

cat > "$mock_bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail

output=""
url=""
while (($#)); do
  case "$1" in
    --output) output="$2"; shift 2 ;;
    --write-out|--user|--retry|--retry-delay) shift 2 ;;
    http*) url="$1"; shift ;;
    *) shift ;;
  esac
done

repository="${url#*/v2/}"
repository="${repository%/tags/list}"
key="${repository//-/_}"
status_var="MOCK_STATUS_${key}"
tags_var="MOCK_TAGS_${key}"
status="${!status_var:-200}"
if [[ "$status" == "404" ]]; then
  printf '404'
  exit 0
fi
if [[ "$status" != "200" ]]; then
  printf '%s' "$status"
  exit 0
fi

printf '{"name":"%s","tags":[%s]}' "$repository" "${!tags_var:-}" > "$output"
printf '200'
MOCK_CURL
chmod +x "$mock_bin/curl"

run_resolver() {
  local branch="$1" image="$2" output="$3"
  shift 3
  : > "$output"
  env \
    "PATH=$mock_bin:$PATH" \
    "REF_NAME=$branch" \
    "IMAGE=$image" \
    "REGISTRY=registry.test" \
    "REGISTRY_USERNAME=" \
    "REGISTRY_PASSWORD=" \
    "SHA=abc123" \
    "GITHUB_OUTPUT=$output" \
    "$@" \
    bash "$resolver"
}

assert_line() {
  local expected="$1" output="$2"
  if ! grep -Fqx -- "$expected" "$output"; then
    echo "Expected output line not found: $expected" >&2
    cat "$output" >&2
    exit 1
  fi
}

output="$temp_dir/dev-max-version"
run_resolver dev barkcloud-web "$output" \
  'MOCK_TAGS_barkcloud_web="1.0.55","1.0.56"' \
  'MOCK_TAGS_barkcloud_web_dev="1.0.1"'
assert_line 'version=1.0.57' "$output"
assert_line 'repository=barkcloud-web-dev' "$output"
assert_line 'tags=registry.test/barkcloud-web-dev:1.0.57,registry.test/barkcloud-web-dev:latest,registry.test/barkcloud-web-dev:abc123' "$output"

output="$temp_dir/dev-repo-missing"
run_resolver dev barkcloud-users "$output" \
  'MOCK_TAGS_barkcloud_users="1.0.16"' \
  'MOCK_STATUS_barkcloud_users_dev=404'
assert_line 'version=1.0.17' "$output"

output="$temp_dir/dev-ahead-of-master"
run_resolver dev barkcloud-web "$output" \
  'MOCK_TAGS_barkcloud_web="1.0.56"' \
  'MOCK_TAGS_barkcloud_web_dev="1.0.57"'
assert_line 'version=1.0.58' "$output"

output="$temp_dir/master-uses-dev-version"
run_resolver master barkcloud-users "$output" \
  'MOCK_TAGS_barkcloud_users_dev="1.0.16","1.0.17"'
assert_line 'version=1.0.17' "$output"
assert_line 'repository=barkcloud-users' "$output"
assert_line 'tag_version=registry.test/barkcloud-users:1.0.17' "$output"

output="$temp_dir/master-without-dev-version"
if run_resolver master barkcloud-files "$output" 'MOCK_STATUS_barkcloud_files_dev=404' \
  2> "$temp_dir/master-without-dev-version.stderr"; then
  echo 'Expected master version resolution to fail without a dev SemVer tag' >&2
  exit 1
fi
if ! grep -Fq 'нет SemVer-тега' "$temp_dir/master-without-dev-version.stderr"; then
  echo 'Expected a clear missing dev SemVer error' >&2
  cat "$temp_dir/master-without-dev-version.stderr" >&2
  exit 1
fi

output="$temp_dir/first-dev-version"
run_resolver dev barkcloud-torrent "$output" \
  'MOCK_STATUS_barkcloud_torrent=404' \
  'MOCK_STATUS_barkcloud_torrent_dev=404'
assert_line 'version=1.0.0' "$output"

echo 'Docker version resolver tests passed.'
