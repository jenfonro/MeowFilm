#!/bin/bash
# Explicit full checks, or a filtered Swift test run with the normal build cache.
set -euo pipefail
cd "$(dirname "$0")/.."
usage() { echo "Usage: $0 all|compile|test --filter <pattern>" >&2; exit 2; }
[ "$#" -gt 0 ] || usage
phase="$1"; shift
case "$phase" in
  all|compile) [ "$#" -eq 0 ] || usage ;;
  test) [ "$#" -eq 2 ] && [ "$1" = --filter ] && [ -n "$2" ] || usage ;;
  *) usage ;;
esac
if [ "$(uname -s)" != Darwin ]; then
  echo "Native Apple checks require macOS." >&2; exit 2
fi
results="$(python3 -I -B scripts/repository-paths.py tmp)/test-results/ios-native"
mkdir -p "$results"
printf 'SCOPE=%s\n' "$phase${2:+:$2}"
status=0
logged() {
  local name="$1"; shift
  "$@" 2>&1 | tee "$results/$name.log"
}
check_tests() {
  if ! grep -Eq 'Executed [1-9][0-9]* tests?' "$results/swift-test.log"; then
    echo "No executed XCTest cases found." >&2; status=4
  fi
  if grep -Eq '[1-9][0-9]* tests? skipped|Test Case .* skipped' "$results/swift-test.log"; then
    echo "Selected tests were skipped." >&2; status=4
  fi
}
if [ "$phase" = test ]; then
  logged javascript-source "${MEOWFILM_BUILD_NODE:-node}" scripts/verify-native-javascript.mjs || exit 4
  logged swift-test swift test --package-path native/MeowKit -j 2 --filter "$2" || status=4
  check_tests
  exit "$status"
fi
arch="$(uname -m)"
settings=(-configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator'
  -jobs 2 ARCHS="$arch" ONLY_ACTIVE_ARCH=YES ENABLE_TESTABILITY=YES
  CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM=)
logged source-scope python3 -I -B scripts/check-source-scope.py || status=4
logged swift-build swift build --build-tests --package-path native/MeowKit -j 2 || status=4
# The App targets cannot build against absent or stale runtime inputs.
logged runtime-inputs python3 -I -B scripts/prepare-ios-runtime.py || exit 4
logged app-build xcodebuild -project apps/ios/Meowfilm.xcodeproj -scheme Meowfilm "${settings[@]}" build || status=4
logged component-build xcodebuild -project apps/ios/Meowfilm.xcodeproj -scheme MeowfilmAcceptance "${settings[@]}" build-for-testing || status=4
app="$(xcodebuild -project apps/ios/Meowfilm.xcodeproj -scheme Meowfilm "${settings[@]}" -showBuildSettings |
  awk -F' = ' '/^[[:space:]]+TARGET_BUILD_DIR = /{directory=$2}
    /^[[:space:]]+FULL_PRODUCT_NAME = /{name=$2}
    END {if (directory != "" && name != "") print directory "/" name}')"
if [ -z "$app" ]; then status=4
else logged simulator-signing python3 -I -B scripts/check-simulator-signing.py "$app" || status=4
fi
[ "$status" -eq 0 ] || exit "$status"
if [ "$phase" = all ]; then
  logged developer-tools python3 -I -B -m unittest discover -s scripts/tests -v || status=4
  logged swift-test swift test --skip-build --package-path native/MeowKit -j 2 || status=4
  check_tests
fi
exit "$status"
