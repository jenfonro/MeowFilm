#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

FRONTEND_DIR="${FRONTEND_DIR:-../MeowFilm-Frontend}"
DST_DIST="public/dist"

# This script only builds the backend binary. It does not build/copy the frontend.
# Ensure the embedded frontend dist exists (it is served from the Go embed at public/dist).
if [[ ! -f "${DST_DIST}/index.html" ]]; then
  echo "missing embedded frontend dist: ${DST_DIST}/index.html" >&2
  echo "run: ./build-all.sh (build frontend + sync to ${DST_DIST} + build backend)" >&2
  exit 1
fi

# Go requires GOCACHE to be an absolute path. Keep it inside the project root.
GOCACHE_DIR="${ROOT_DIR}/.gocache"
mkdir -p "${GOCACHE_DIR}"
export GOCACHE="${GOCACHE_DIR}"

# Optional: keep module caches inside the project root.
# NOTE: enabling this may trigger module downloads if the local cache is empty.
if [[ "${MEOWFILM_GO_LOCAL_CACHE:-}" == "1" ]]; then
  GOMODCACHE_DIR="${ROOT_DIR}/.gomodcache"
  mkdir -p "${GOMODCACHE_DIR}"
  export GOMODCACHE="${GOMODCACHE_DIR}"

  GOPATH_DIR="${ROOT_DIR}/.gopath"
  mkdir -p "${GOPATH_DIR}"
  export GOPATH="${GOPATH_DIR}"
fi

BUILD_DIR="${ROOT_DIR}/build"
mkdir -p "${BUILD_DIR}"

BACKEND_COMMIT=""
FRONTEND_COMMIT=""
# By default, local builds should look like "beta-<timestamp>" in the UI (see README).
# Only embed git commits when explicitly enabled (or when ASSET_VERSION is set for release-like builds).
EMBED_COMMITS="${MEOWFILM_EMBED_COMMITS:-}"
if [[ -z "${EMBED_COMMITS}" ]] && [[ -n "${ASSET_VERSION:-}" ]]; then
  EMBED_COMMITS="1"
fi

LDFLAGS=""
if [[ "${EMBED_COMMITS}" == "1" ]] && command -v git >/dev/null 2>&1; then
  BACKEND_COMMIT="$(git -C "${ROOT_DIR}" rev-parse --short HEAD 2>/dev/null || true)"
  FRONTEND_COMMIT="$(git -C "${FRONTEND_DIR}" rev-parse --short HEAD 2>/dev/null || true)"
  if [[ -n "${BACKEND_COMMIT}" ]]; then
    LDFLAGS+=" -X github.com/jenfonro/meowfilm/server/static.BuildBackendCommit=${BACKEND_COMMIT}"
  fi
  if [[ -n "${FRONTEND_COMMIT}" ]]; then
    LDFLAGS+=" -X github.com/jenfonro/meowfilm/server/static.BuildFrontendCommit=${FRONTEND_COMMIT}"
  fi
  LDFLAGS="${LDFLAGS# }"
fi

if [[ -n "${LDFLAGS}" ]]; then
  CGO_ENABLED=1 go build -ldflags "${LDFLAGS}" -o "${BUILD_DIR}/meowfilm" .
else
  CGO_ENABLED=1 go build -o "${BUILD_DIR}/meowfilm" .
fi
echo "built: ${BUILD_DIR}/meowfilm"
