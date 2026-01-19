#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
cd "${ROOT_DIR}"

FRONTEND_DIR="../TV_Server-Frontend"
FRONTEND_REPO_URL="https://github.com/jenfonro/TV_Server-Frontend"

if [[ ! -d "${FRONTEND_DIR}" ]]; then
  if ! command -v git >/dev/null 2>&1; then
    echo "missing frontend dir: ${FRONTEND_DIR}" >&2
    echo "git not found; cannot auto-clone: ${FRONTEND_REPO_URL}" >&2
    exit 1
  fi
  echo "frontend not found, cloning: ${FRONTEND_REPO_URL} -> ${FRONTEND_DIR}" >&2
  git clone --depth 1 "${FRONTEND_REPO_URL}" "${FRONTEND_DIR}"
fi

SRC_DIST="${FRONTEND_DIR}/dist"
DST_DIST="public/dist"

if [[ ! -d "${SRC_DIST}" ]]; then
  echo "missing frontend dist: ${SRC_DIST}" >&2
  echo "run: cd ${FRONTEND_DIR} && npm i && npm run build" >&2
  exit 1
fi

rm -rf "${DST_DIST}"
mkdir -p "${DST_DIST}"
cp -a "${SRC_DIST}/." "${DST_DIST}/"

mkdir -p "../.gocache"
export GOCACHE="../.gocache"

go build -o "tvserver" .
echo "built: tvserver"
