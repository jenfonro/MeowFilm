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

go build -o "${BUILD_DIR}/meowfilm" .
echo "built: ${BUILD_DIR}/meowfilm"
