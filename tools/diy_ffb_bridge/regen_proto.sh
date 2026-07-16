#!/usr/bin/env bash
# Regenerate the Python protobuf bindings from the authoritative .proto.
# Run from anywhere; paths are resolved relative to this script.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
protoc --python_out="$here" --proto_path="$repo/proto" "$repo/proto/diy_ffb_protocol.proto"
echo "regenerated diy_ffb_protocol_pb2.py"
