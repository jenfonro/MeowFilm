#!/usr/bin/env bash
set -euo pipefail

# All paths come from the checked driver, not ambient GCC/MSVC or the VLC builder.
source_dir=$(cygpath -u "$1")
build_dir=$(cygpath -u "$2")
prefix=$(cygpath -u "$3")
tools_dir=$(cygpath -u "$4")
jobs=$5
configuration=$6
export PYTHONDONTWRITEBYTECODE=1
export LC_ALL=C
export MSYS2_ARG_CONV_EXCL='*'
export PATH="$tools_dir:$PATH"
export PKG_CONFIG_PATH="$(cygpath -m "$prefix")/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$PKG_CONFIG_PATH"
export PKG_CONFIG_SYSTEM_INCLUDE_PATH=
export PKG_CONFIG_SYSTEM_LIBRARY_PATH=

meson_cmd() {
    if [[ "$MFK_MESON" == *.py ]]; then
        "$MFK_PYTHON" -B "$MFK_MESON" "$@"
    else
        "$MFK_MESON" "$@"
    fi
}

pkg_config() {
    if [[ -n "${MFK_PKG_PERL:-}" ]]; then
        if [[ "$MFK_PKG_CONFIG" == *.bat ]]; then
            "$MFK_PKG_PERL" -x "$MFK_PKG_CONFIG" "$@"
        else
            "$MFK_PKG_PERL" "$MFK_PKG_CONFIG" "$@"
        fi
    else
        "$MFK_PKG_CONFIG" "$@"
    fi
}
export -f pkg_config

cmake_build() {
    local name=$1
    shift
    local project="$source_dir/$name"
    [[ "$name" != bzip2 && "$name" != zlib && "$name" != unibreak ]] || project="$build_dir/compression-project"
    "$MFK_CMAKE" -S "$(cygpath -m "$project")" -B "$(cygpath -m "$build_dir/$name")" \
        -G Ninja "-DCMAKE_MAKE_PROGRAM=$MFK_NINJA" \
        "-DCMAKE_C_COMPILER=$MFK_CC" "-DCMAKE_CXX_COMPILER=$MFK_CXX" \
        "-DCMAKE_RC_COMPILER=$MFK_WINDRES" "-DCMAKE_BUILD_TYPE=$configuration" \
        "-DCMAKE_INSTALL_PREFIX=$(cygpath -m "$prefix")" -DCMAKE_INSTALL_LIBDIR=lib \
        "-DCMAKE_PREFIX_PATH=$(cygpath -m "$prefix")" -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF \
        -DCMAKE_C_FLAGS=-D_WIN32_WINNT=0x0A00 \
        -DCMAKE_CXX_FLAGS=-D_WIN32_WINNT=0x0A00 \
        '-DCMAKE_EXE_LINKER_FLAGS=-static -static-libgcc -static-libstdc++' "$@"
    "$MFK_CMAKE" --build "$(cygpath -m "$build_dir/$name")" --parallel "$jobs"
    "$MFK_CMAKE" --install "$(cygpath -m "$build_dir/$name")"
}

meson_build() {
    local name=$1
    shift
    local buildtype=release
    [[ "$configuration" != Debug ]] || buildtype=debug
    if [[ -f "$build_dir/$name/meson-private/coredata.dat" ]]; then
        meson_cmd setup --reconfigure "$(cygpath -m "$build_dir/$name")" \
            "$(cygpath -m "$source_dir/$name")" "$@"
    else
        meson_cmd setup "$(cygpath -m "$build_dir/$name")" "$(cygpath -m "$source_dir/$name")" \
            "--native-file=$(cygpath -m "$build_dir/native.ini")" \
            "--prefix=$(cygpath -m "$prefix")" --libdir=lib "--buildtype=$buildtype" \
            --default-library=static --wrap-mode=nodownload -Dprefer_static=true -Dauto_features=disabled "$@"
    fi
    local targets=()
    # The unused amalgamation generators exceed CreateProcess's argument limit.
    [[ "$name" != harfbuzz ]] || targets=(harfbuzz harfbuzz-subset)
    meson_cmd compile -C "$(cygpath -m "$build_dir/$name")" -j "$jobs" "${targets[@]}"
    meson_cmd install -C "$(cygpath -m "$build_dir/$name")" --no-rebuild
}
