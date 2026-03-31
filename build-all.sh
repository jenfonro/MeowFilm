#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

FRONTEND_REPO_URL="${FRONTEND_REPO_URL:-}"
FRONTEND_DIR="${FRONTEND_DIR:-../MeowFilm-Frontend}"

if [[ ! -d "${FRONTEND_DIR}" ]]; then
  echo "missing frontend dir: ${FRONTEND_DIR}; skip frontend build and build backend only" >&2
  exec "${SCRIPT_DIR}/build.sh"
fi

if ! command -v npm >/dev/null 2>&1; then
  echo "npm not found; skip frontend build and build backend only" >&2
  exec "${SCRIPT_DIR}/build.sh"
fi

(cd "${FRONTEND_DIR}" && npm ci && npm run build)

# Always sync the freshly built frontend dist into the embedded `public/dist`.
SRC_DIST="${FRONTEND_DIR}/dist"
DST_DIST="public/dist"
if [[ ! -d "${SRC_DIST}" ]]; then
  echo "missing frontend dist: ${SRC_DIST}" >&2
  exit 1
fi
rm -rf "${DST_DIST}"
mkdir -p "${DST_DIST}"
cp -a "${SRC_DIST}/." "${DST_DIST}/"

# QuickJS uses cgo.
export CGO_ENABLED=1

exec "${SCRIPT_DIR}/build.sh"
