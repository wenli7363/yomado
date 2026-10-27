#!/bin/bash
set -euo pipefail
ym_topology_root="$(cd "$(dirname "$0")/.." && pwd -P)"
ym_topology_stage="$(mktemp -d "${TMPDIR:-/tmp}/Yomado-topology-tests.XXXXXX")"
trap 'rm -rf "$ym_topology_stage"' EXIT
ym_topology_developer="${YOMADO_DEVELOPER_DIR:-$(xcode-select -p)}"
ym_topology_clang="$(DEVELOPER_DIR="$ym_topology_developer" /usr/bin/xcrun --find clang)"
ym_topology_sdk="$(DEVELOPER_DIR="$ym_topology_developer" /usr/bin/xcrun --sdk macosx --show-sdk-path)"
"$ym_topology_clang" -fobjc-arc -Wall -Wextra -Werror -isysroot "$ym_topology_sdk" \
  -mmacosx-version-min=14.0 -ffile-prefix-map="$ym_topology_root"=. -fmacro-prefix-map="$ym_topology_root"=. \
  -I "$ym_topology_root/bridge" "$ym_topology_root/tests/DisplayTopologyTests.m" \
  "$ym_topology_root/bridge/YMDisplayTopology.m" -framework Foundation -framework CoreGraphics -framework ColorSync \
  -o "$ym_topology_stage/DisplayTopologyTests"
"$ym_topology_stage/DisplayTopologyTests"
