#!/bin/bash
# Native Apple boundary, build, linkage, and Simulator signing verification.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "$#" -ne 0 ]; then
  echo "Usage: $0" >&2
  exit 2
fi
if [ "$(uname -s)" != Darwin ]; then
  echo "Native Apple checks require macOS." >&2
  exit 2
fi
results="$(python3 -I -B scripts/repository-paths.py tmp)/verify-native"
mkdir -p "$results"
status=0
logged() {
  local name="$1"; shift
  "$@" 2>&1 | tee "$results/$name.log"
}
arch="$(uname -m)"
settings=(-configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator'
  -jobs 2 ARCHS="$arch" ONLY_ACTIVE_ARCH=YES
  CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM=)
logged source-scope python3 -I -B scripts/check-source-scope.py || status=4
logged storage-schema python3 -I -B scripts/storage/generate-schema.py --check || status=4
logged sqlite-kernel python3 -I -B scripts/prepare-ios-sqlite.py --target macos || exit 4
logged audio-kernel python3 -I -B scripts/prepare-ios-audio-kernel.py --target "macos-$arch" --target "ios-simulator-$arch" || exit 4
logged video-kernel python3 -I -B scripts/prepare-ios-video-kernel.py --target "macos-$arch" --target "ios-simulator-$arch" || exit 4
logged aether-kernel python3 -I -B scripts/prepare-ios-aether-kernel.py --target "macos-$arch" --target "ios-simulator-$arch" || exit 4
logged sqlite-simulator python3 -I -B scripts/prepare-ios-sqlite.py --target ios-simulator || exit 4
logged swift-build swift build --package-path native/MeowKit -j 2 || status=4
# The App target cannot build against absent or stale runtime inputs.
logged runtime-inputs python3 -I -B scripts/prepare-ios-runtime.py --sdk iphonesimulator --arch "$arch" || exit 4
logged app-build xcodebuild -project apps/ios/Meowfilm.xcodeproj -scheme Meowfilm "${settings[@]}" build || status=4
app="$(xcodebuild -project apps/ios/Meowfilm.xcodeproj -scheme Meowfilm "${settings[@]}" -showBuildSettings |
  awk -F' = ' '/^[[:space:]]+TARGET_BUILD_DIR = /{directory=$2}
    /^[[:space:]]+FULL_PRODUCT_NAME = /{name=$2}
    END {if (directory != "" && name != "") print directory "/" name}')"
if [ -z "$app" ]; then status=4
else
  logged aether-linkage python3 -I -B scripts/prepare-ios-aether-kernel.py --target "ios-simulator-$arch" --verify-app "$app" || status=4
  logged simulator-signing python3 -I -B scripts/check-simulator-signing.py "$app" || status=4
fi
exit "$status"
