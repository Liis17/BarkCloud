#!/usr/bin/env bash
# Генерация Swift gRPC/protobuf-стабов из Shared/BarkCloud.Proto/ прямо в
# исходники пакета BarkCloudKit. Зеркалит Ios/BarkCloud/sync_proto.sh, но
# выводит в Sources/BarkCloudKit/Generated/ и с Visibility=Public (типы пакета
# должны быть видны зависимым таргетам — контейнер-app и FSKit-расширению).
#
# Требует (на Mac):
#   brew install protobuf swift-protobuf
#   brew install grpc-swift   # protoc-gen-grpc-swift-2
#
# Запуск: Mac/BarkCloudKit/scripts/sync_proto.sh   (идемпотентно)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${HERE}/../../../Shared/BarkCloud.Proto"
PROTO_DIR="${HERE}/../Proto"
GEN_DIR="${HERE}/../Sources/BarkCloudKit/Generated"

mkdir -p "${PROTO_DIR}" "${GEN_DIR}"

# Только клиентские .proto (configuration_api — серверный, пропускаем).
PROTOS=(identity_api.proto users_api.proto files_api.proto shared.proto)
for f in "${PROTOS[@]}"; do
    if [ -f "${SRC}/${f}" ]; then
        rsync -a "${SRC}/${f}" "${PROTO_DIR}/${f}"
    else
        echo "warning: ${SRC}/${f} not found" >&2
    fi
done

PROTOC="${PROTOC:-$(command -v protoc 2>/dev/null || echo /opt/homebrew/bin/protoc)}"
SWIFT_GEN="${SWIFT_GEN:-$(command -v protoc-gen-swift 2>/dev/null || echo /opt/homebrew/bin/protoc-gen-swift)}"
GRPC_GEN="${GRPC_GEN:-$(command -v protoc-gen-grpc-swift-2 2>/dev/null || echo /opt/homebrew/bin/protoc-gen-grpc-swift-2)}"

if [ ! -x "${PROTOC}" ] || [ ! -x "${SWIFT_GEN}" ] || [ ! -x "${GRPC_GEN}" ]; then
    echo "error: protoc / protoc-gen-swift / protoc-gen-grpc-swift-2 not found." >&2
    echo "       install via: brew install protobuf swift-protobuf grpc-swift" >&2
    exit 1
fi

for f in "${PROTO_DIR}"/*.proto; do
    [ -f "$f" ] || continue
    "${PROTOC}" \
        --plugin=protoc-gen-swift="${SWIFT_GEN}" \
        --plugin=protoc-gen-grpc-swift-2="${GRPC_GEN}" \
        --swift_out="${GEN_DIR}" \
        --swift_opt=Visibility=Public \
        --grpc-swift-2_out="${GEN_DIR}" \
        --grpc-swift-2_opt=Visibility=Public \
        --proto_path="${PROTO_DIR}" \
        "$f"
done
