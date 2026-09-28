#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

FRONTEND_DIR="${ROOT_DIR}/frontend"

(cd "${FRONTEND_DIR}" && npm ci && npm run build)

# Always sync the freshly built frontend dist into the embedded `public/dist`.
SRC_DIST="${FRONTEND_DIR}/dist"
DST_DIST="${ROOT_DIR}/backend/public/dist"
if [[ ! -d "${SRC_DIST}" ]]; then
  echo "missing frontend dist: ${SRC_DIST}" >&2
  exit 1
fi
rm -rf "${DST_DIST}"
mkdir -p "${DST_DIST}"
cp -a "${SRC_DIST}/." "${DST_DIST}/"

# QuickJS uses cgo.
export CGO_ENABLED=1

exec bash "${SCRIPT_DIR}/build.sh"
