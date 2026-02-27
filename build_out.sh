#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

# "Out" build (for distribution): builds the backend binary with the user limit enabled,
# and writes the artifact outside the backend repo directory.
#
# Notes:
# - This script only builds the backend binary. It does not build/copy the frontend.
# - Ensure the embedded frontend dist exists (served from Go embed at public/dist).

FRONTEND_DIR="${FRONTEND_DIR:-../MeowFilm-Frontend}"
DST_DIST="public/dist"

if [[ ! -f "${DST_DIST}/index.html" ]]; then
  echo "missing embedded frontend dist: ${DST_DIST}/index.html" >&2
  echo "run: ./build-all.sh (build frontend + sync to ${DST_DIST} + build backend)" >&2
  exit 1
fi

# Go requires GOCACHE to be an absolute path. Keep it inside the project root.
GOCACHE_DIR="${ROOT_DIR}/.gocache"
mkdir -p "${GOCACHE_DIR}"
export GOCACHE="${GOCACHE_DIR}"

if [[ "${MEOWFILM_GO_LOCAL_CACHE:-}" == "1" ]]; then
  GOMODCACHE_DIR="${ROOT_DIR}/.gomodcache"
  mkdir -p "${GOMODCACHE_DIR}"
  export GOMODCACHE="${GOMODCACHE_DIR}"

  GOPATH_DIR="${ROOT_DIR}/.gopath"
  mkdir -p "${GOPATH_DIR}"
  export GOPATH="${GOPATH_DIR}"
fi

OUT_DIR="${OUT_DIR:-${ROOT_DIR}/out_build}"
mkdir -p "${OUT_DIR}"

WATERMARK="${MEOWFILM_WATERMARK:-}"
if [[ -z "${WATERMARK}" ]]; then
  # 16 hex chars.
  WATERMARK="$(LC_ALL=C tr -dc 'a-f0-9' </dev/urandom | head -c 16 || true)"
fi
if [[ -z "${WATERMARK}" ]]; then
  echo "failed to generate watermark; set MEOWFILM_WATERMARK" >&2
  exit 1
fi

BACKEND_COMMIT=""
FRONTEND_COMMIT=""
EMBED_COMMITS="1"

LDFLAGS="-s -w"
LDFLAGS+=" -X github.com/jenfonro/meowfilm/internal/buildinfo.Watermark=${WATERMARK}"
if [[ "${EMBED_COMMITS}" == "1" ]] && command -v git >/dev/null 2>&1; then
  BACKEND_COMMIT="$(git -C "${ROOT_DIR}" rev-parse --short HEAD 2>/dev/null || true)"
  FRONTEND_COMMIT="$(git -C "${FRONTEND_DIR}" rev-parse --short HEAD 2>/dev/null || true)"
  if [[ -n "${BACKEND_COMMIT}" ]]; then
    LDFLAGS+=" -X github.com/jenfonro/meowfilm/server/static.BuildBackendCommit=${BACKEND_COMMIT}"
  fi
  if [[ -n "${FRONTEND_COMMIT}" ]]; then
    LDFLAGS+=" -X github.com/jenfonro/meowfilm/server/static.BuildFrontendCommit=${FRONTEND_COMMIT}"
  fi
fi

OUT_BIN="${OUT_DIR}/meowfilm_${WATERMARK}"

# Obfuscation: default enabled.
# - If `garble` exists, use it.
# - Otherwise, try to install it into a project-local tools dir.
# - If install fails (offline env), fall back to plain `go build`.
GO_TOOL="go"
TOOLS_DIR="${ROOT_DIR}/.tools"
TOOLS_BIN="${TOOLS_DIR}/bin"
mkdir -p "${TOOLS_BIN}"
export PATH="${TOOLS_BIN}:${PATH}"

if ! command -v garble >/dev/null 2>&1; then
  echo "garble not found; installing..." >&2
  # Install to local tools bin to avoid depending on global GOPATH/GOBIN permissions.
  GOBIN="${TOOLS_BIN}" go install mvdan.cc/garble@latest >/dev/null 2>&1 || true
fi
if command -v garble >/dev/null 2>&1; then
  GO_TOOL="garble"
else
  echo "garble install failed; building without obfuscation" >&2
fi

BUILD_MODE_ARGS=("-buildmode=pie")

set +e
CGO_ENABLED=1 "${GO_TOOL}" build "${BUILD_MODE_ARGS[@]}" -tags userlimit -ldflags "${LDFLAGS}" -o "${OUT_BIN}" .
rc=$?
set -e
if [[ $rc -ne 0 ]]; then
  echo "pie build failed; retry without -buildmode=pie" >&2
  CGO_ENABLED=1 "${GO_TOOL}" build -tags userlimit -ldflags "${LDFLAGS}" -o "${OUT_BIN}" .
fi
cp -f "${OUT_BIN}" "${OUT_DIR}/meowfilm"
echo "built: ${OUT_BIN}"
echo "built: ${OUT_DIR}/meowfilm"
