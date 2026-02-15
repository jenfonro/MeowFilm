#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

FRONTEND_REPO_URL="${FRONTEND_REPO_URL:-}"
FRONTEND_DIR="${FRONTEND_DIR:-../MeowFilm-Frontend}"

SRC_DIST="${FRONTEND_DIR}/dist"
DST_DIST="public/dist"

SYNC_FRONTEND_DIST="${MEOWFILM_SYNC_FRONTEND_DIST:-}"

# Prefer building with an already-prepared ${DST_DIST} (e.g. in CI, or when dist is committed/copied in advance).
if [[ -z "${SYNC_FRONTEND_DIST}" ]]; then
  if [[ -f "${DST_DIST}/index.html" ]]; then
    echo "using existing frontend dist: ${DST_DIST}"
  else
    echo "missing frontend dist: ${DST_DIST}" >&2
    echo "either provide ${DST_DIST} (recommended), or set MEOWFILM_SYNC_FRONTEND_DIST=1 to copy from ${SRC_DIST}" >&2
    exit 1
  fi
else
  if [[ ! -d "${SRC_DIST}" ]]; then
    echo "missing frontend dist: ${SRC_DIST}" >&2
    echo "run: cd ${FRONTEND_DIR} && npm ci && npm run build" >&2
    exit 1
  fi

  rm -rf "${DST_DIST}"
  mkdir -p "${DST_DIST}"
  cp -a "${SRC_DIST}/." "${DST_DIST}/"
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
