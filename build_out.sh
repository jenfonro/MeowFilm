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
STRICT_PROTECTED="${MEOWFILM_STRICT_PROTECTED:-0}"

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
# In strict mode, garble is mandatory and any fallback is forbidden.
GO_TOOL="go"
TOOLS_DIR="${ROOT_DIR}/.tools"
TOOLS_BIN="${TOOLS_DIR}/bin"
mkdir -p "${TOOLS_BIN}"
export PATH="${TOOLS_BIN}:${PATH}"

if ! command -v garble >/dev/null 2>&1; then
  echo "garble not found; installing..." >&2
  # Install to local tools bin to avoid depending on global GOPATH/GOBIN permissions.
  if ! GOBIN="${TOOLS_BIN}" go install mvdan.cc/garble@latest >/dev/null 2>&1; then
    if [[ "${STRICT_PROTECTED}" == "1" ]]; then
      echo "garble install failed in strict mode" >&2
      exit 1
    fi
  fi
fi
if command -v garble >/dev/null 2>&1; then
  GO_TOOL="garble"
else
  if [[ "${STRICT_PROTECTED}" == "1" ]]; then
    echo "garble not available in strict mode" >&2
    exit 1
  fi
  echo "garble install failed; building without obfuscation" >&2
fi

BUILD_MODE_ARGS=()
if [[ "${GOOS:-}" != "windows" ]]; then
  BUILD_MODE_ARGS=("-buildmode=pie")
fi

# Windows quickjs-go static lib needs pthread symbols and __p__environ shim.
if [[ "${GOOS:-}" == "windows" ]]; then
  if [[ -z "${CC:-}" ]]; then
    echo "windows build requires CC (e.g. x86_64-w64-mingw32-gcc)" >&2
    exit 1
  fi
  if [[ "${GOARCH:-}" != "amd64" ]]; then
    echo "unsupported windows GOARCH in build_out.sh: ${GOARCH:-}" >&2
    exit 1
  fi

  ar_bin="${AR:-x86_64-w64-mingw32-ar}"
  if ! command -v "${ar_bin}" >/dev/null 2>&1; then
    echo "missing archiver: ${ar_bin}" >&2
    exit 1
  fi

  shim_dir="$(mktemp -d /tmp/mf_win_shim.XXXXXX)"
  trap 'rm -rf "${shim_dir}"' EXIT
  shim_c="${shim_dir}/environ_shim.c"
  shim_o="${shim_dir}/environ_shim.o"
  shim_a="${shim_dir}/libenviron_shim.a"
  cat > "${shim_c}" <<'EOF'
#ifdef _WIN32
static char **mf_dummy_environ = 0;
char ***__p__environ(void) { return &mf_dummy_environ; }
void *__imp___p__environ = (void *)&__p__environ;
#endif
EOF
  "${CC}" -c "${shim_c}" -o "${shim_o}"
  "${ar_bin}" rcs "${shim_a}" "${shim_o}"
  LDFLAGS+=" -extldflags \"-L${shim_dir} -lenviron_shim -lwinpthread -lmsvcrt\""
fi

ext=""
if [[ "${GOOS:-}" == "windows" ]]; then ext=".exe"; fi
OUT_BIN="${OUT_DIR}/meowfilm_${WATERMARK}${ext}"
OUT_MAIN="${OUT_DIR}/meowfilm${ext}"

CGO_ENABLED=1 "${GO_TOOL}" build "${BUILD_MODE_ARGS[@]}" -tags userlimit -ldflags "${LDFLAGS}" -o "${OUT_BIN}" .
cp -f "${OUT_BIN}" "${OUT_MAIN}"
echo "built: ${OUT_BIN}"
echo "built: ${OUT_MAIN}"
