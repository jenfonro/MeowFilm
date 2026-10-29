#!/usr/bin/env bash
# The iOS product build entry: build and verify in tmp/, then replace build/ios/.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"
if [ "$(uname -s)" != Darwin ]; then
  echo "iOS builds require macOS." >&2
  exit 2
fi

tmp="$(python3 -I -B scripts/repository-paths.py tmp)"
build="$(python3 -I -B scripts/repository-paths.py build)"
mkdir -p "$tmp"
lock="$tmp/build-ios.lock"
if ! mkdir "$lock" 2>/dev/null; then
  echo "Another iOS build is using this checkout: $lock" >&2
  exit 75
fi
staging="$tmp/build-ios-$$"
previous=""
cleanup() {
  rm -rf "$staging"
  if [ -n "$previous" ]; then rm -rf "$previous"; fi
  rmdir "$lock"
}
trap cleanup EXIT
mkdir "$staging"

settings=(
  -project apps/ios/Meowfilm.xcodeproj
  -scheme Meowfilm
  -configuration Release
  -sdk iphoneos
  -destination 'generic/platform=iOS'
  ARCHS=arm64 ONLY_ACTIVE_ARCH=NO
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO
  CODE_SIGN_IDENTITY= DEVELOPMENT_TEAM=
)
xcodebuild -version > "$staging/toolchain.txt"
xcodebuild "${settings[@]}" -jobs 2 build 2>&1 | tee "$tmp/build-ios.log"
app="$(xcodebuild "${settings[@]}" -showBuildSettings |
  awk -F' = ' '/^[[:space:]]+TARGET_BUILD_DIR = /{directory=$2}
    /^[[:space:]]+FULL_PRODUCT_NAME = /{name=$2}
    END {if (directory != "" && name != "") print directory "/" name}')"
if [ -z "$app" ]; then
  echo "Unable to locate the built app." >&2
  exit 4
fi
python3 -I -B scripts/package-beta-ios.py "$app" "$staging"

mkdir -p "$(dirname "$build")"
if [ -e "$build" ]; then
  previous="$tmp/build-ios-previous-$$"
  mv "$build" "$previous"
fi
if ! mv "$staging/build" "$build"; then
  if [ -n "$previous" ]; then mv "$previous" "$build"; previous=""; fi
  exit 5
fi
printf 'BUILD=%s\n' "$build"
