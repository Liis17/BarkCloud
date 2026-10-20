#!/usr/bin/env bash
set -euo pipefail

case "$REF_NAME" in
  master) repository="$IMAGE" ;;
  dev) repository="${IMAGE}-dev" ;;
  *) echo "Ветка $REF_NAME не поддерживает публикацию BarkCloud-образов" >&2; exit 1 ;;
esac

response_file="$(mktemp)"
trap 'rm -f "$response_file"' EXIT

latest_version() {
  local image_repository="$1"
  local http_code tags_json

  if [ -n "$REGISTRY_USERNAME" ] && [ -n "$REGISTRY_PASSWORD" ]; then
    http_code="$(curl --silent --show-error --retry 3 --retry-delay 15 \
      --user "$REGISTRY_USERNAME:$REGISTRY_PASSWORD" \
      --output "$response_file" --write-out '%{http_code}' \
      "https://${REGISTRY}/v2/${image_repository}/tags/list")" || {
      echo "Не удалось связаться с Docker Registry" >&2
      exit 1
    }
  else
    http_code="$(curl --silent --show-error --retry 3 --retry-delay 15 \
      --output "$response_file" --write-out '%{http_code}' \
      "https://${REGISTRY}/v2/${image_repository}/tags/list")" || {
      echo "Не удалось связаться с Docker Registry" >&2
      exit 1
    }
  fi

  case "$http_code" in
    200) tags_json="$(<"$response_file")" ;;
    404) tags_json="" ;;
    *) echo "Docker Registry вернул HTTP ${http_code} для ${image_repository}" >&2; exit 1 ;;
  esac

  if [ -n "$tags_json" ]; then
    printf '%s' "$tags_json" \
      | grep -oE '"[0-9]+\.[0-9]+\.[0-9]+"' \
      | tr -d '"' \
      | sort -V \
      | tail -n 1 || true
  fi
}

case "$REF_NAME" in
  dev)
    master_version="$(latest_version "$IMAGE")"
    dev_version="$(latest_version "${IMAGE}-dev")"
    latest="$(printf '%s\n%s\n' "$master_version" "$dev_version" | sort -V | tail -n 1)"
    if [ -z "$latest" ]; then
      version="1.0.0"
    else
      IFS=. read -r major minor patch <<< "$latest"
      version="${major}.${minor}.$((patch + 1))"
    fi
    ;;
  master)
    version="$(latest_version "${IMAGE}-dev")"
    if [ -z "$version" ]; then
      echo "У dev-образа ${IMAGE}-dev нет SemVer-тега для публикации master" >&2
      exit 1
    fi
    ;;
esac

tag_version="${REGISTRY}/${repository}:${version}"
tag_latest="${REGISTRY}/${repository}:latest"
tag_sha="${REGISTRY}/${repository}:${SHA}"
{
  echo "version=${version}"
  echo "repository=${repository}"
  echo "tags=${tag_version},${tag_latest},${tag_sha}"
  echo "tag_sha=${tag_sha}"
  echo "tag_version=${tag_version}"
} >> "$GITHUB_OUTPUT"
