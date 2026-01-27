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

if ! command -v npm >/dev/null 2>&1; then
  echo "npm not found; cannot build frontend" >&2
  exit 1
fi

(cd "${FRONTEND_DIR}" && npm ci && npm run build)

exec "${SCRIPT_DIR}/build.sh"
