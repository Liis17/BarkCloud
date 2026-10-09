#!/usr/bin/env bash
set -euo pipefail
f18_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
f18_project="${BARKCLOUD_F18_PROJECT:-barkcloud-f18}"
f18_compose=(docker compose -p "$f18_project" -f "$f18_dir/docker-compose.yml")
"${f18_compose[@]}" up -d --wait
# Keep both volumes on exit, including failures, for inspection and restart tests.
trap '"${f18_compose[@]}" stop' EXIT
export BARKCLOUD_TEST_POSTGRES='Host=127.0.0.1;Port=55418;Database=postgres;Username=postgres;Password=postgres'
export BARKCLOUD_TEST_RABBITMQ='rabbitmq://integration:integration@127.0.0.1:56718/'
export BARKCLOUD_TEST_RABBITMQ_CONTAINER="$("${f18_compose[@]}" ps -q rabbitmq)"
"${f18_compose[@]}" exec -T rabbitmq rabbitmqctl version
dotnet test "$f18_dir/BarkCloud.Files.IntegrationTests.csproj" -c Release \
  --logger 'console;verbosity=normal' --logger 'trx;LogFileName=files-upload-integration.trx' \
  --results-directory "$f18_dir/TestResults" "$@"
