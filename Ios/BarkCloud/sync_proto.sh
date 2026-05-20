#!/usr/bin/env bash
# Run-Script build phase: syncs Shared/BarkCloud.Proto/*.proto into Ios/BarkCloud/Proto/
# (kept OUTSIDE the BarkCloud/ source folder so Xcode does not try to compile them as
# Swift), then runs protoc with swift-protobuf + grpc-swift-2 plugins to generate
# Swift stubs into BarkCloud/Generated/Proto/ (picked up by the filesystem-synchronized
# group and compiled into the app target).
#
# Requires:
#   brew install protobuf swift-protobuf
#   brew install grpc-swift   # provides protoc-gen-grpc-swift-2
#
# Idempotent — safe to run on every build.
set -euo pipefail

SRC="${SRCROOT}/../../Shared/BarkCloud.Proto"
PROTO_DIR="${SRCROOT}/Proto"
GEN_DIR="${SRCROOT}/BarkCloud/Generated/Proto"

mkdir -p "${PROTO_DIR}" "${GEN_DIR}"

# Sync client-relevant .proto files only (skip configuration_api which is server-only).
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
        --swift_opt=Visibility=Internal \
        --grpc-swift-2_out="${GEN_DIR}" \
        --grpc-swift-2_opt=Visibility=Internal \
        --proto_path="${PROTO_DIR}" \
        "$f"
done
