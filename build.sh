#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

FRONTEND_REPO_URL="${FRONTEND_REPO_URL:-}"
FRONTEND_DIR="${FRONTEND_DIR:-../MeowFilm-Frontend}"

if [[ ! -d "${FRONTEND_DIR}" ]]; then
  echo "missing frontend dir: ${FRONTEND_DIR}" >&2
  if [[ -n "${FRONTEND_REPO_URL}" ]] && command -v git >/dev/null 2>&1; then
    echo "frontend not found, cloning: ${FRONTEND_REPO_URL} -> ${FRONTEND_DIR}" >&2
    git clone --depth 1 "${FRONTEND_REPO_URL}" "${FRONTEND_DIR}"
  else
    echo "set FRONTEND_DIR to your frontend folder (and optional FRONTEND_REPO_URL for auto-clone)" >&2
    exit 1
  fi
fi

SRC_DIST="${FRONTEND_DIR}/dist"
DST_DIST="public/dist"

if [[ ! -d "${SRC_DIST}" ]]; then
  echo "missing frontend dist: ${SRC_DIST}" >&2
  echo "run: cd ${FRONTEND_DIR} && npm ci && npm run build" >&2
  exit 1
fi

rm -rf "${DST_DIST}"
mkdir -p "${DST_DIST}"
cp -a "${SRC_DIST}/." "${DST_DIST}/"

# Go requires GOCACHE to be an absolute path. Keep it inside the project root.
GOCACHE_DIR="${ROOT_DIR}/.gocache"
mkdir -p "${GOCACHE_DIR}"
export GOCACHE="${GOCACHE_DIR}"

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
  go build -ldflags "${LDFLAGS}" -o "${BUILD_DIR}/meowfilm" .
else
  go build -o "${BUILD_DIR}/meowfilm" .
fi
echo "built: ${BUILD_DIR}/meowfilm"
