#!/usr/bin/env bash
set -euo pipefail

source_dir=$(cygpath -u "$1")
build_dir=$(cygpath -u "$2")
prefix=$(cygpath -u "$3")
xml_prefix=$(cygpath -u "$4")
compiler=$(cygpath -u "$5")
make=$(cygpath -u "$6")
export MVK_PERL=$(cygpath -u "$7")
export MVK_PKG_CONFIG=$(cygpath -u "$8")
flags_file=$(cygpath -u "$9")
fingerprint=${10}
jobs=${11}

# This is the installed, real pkg-config implementation, not a fake feature probe.
meowfilm_pkg_config() { "$MVK_PERL" "$MVK_PKG_CONFIG" "$@"; }
export -f meowfilm_pkg_config
export PKG_CONFIG_PATH="$xml_prefix/lib/pkgconfig"
export LC_ALL=C
mkdir -p "$build_dir"
cd "$build_dir"
flags=()
while IFS= read -r flag; do flags+=("$flag"); done < "$flags_file"
if [[ ! -f ffbuild/config.mak || ! -f configure.identity || "$(cat configure.identity)" != "$fingerprint" ]]; then
    bash "$source_dir/configure" "${flags[@]}" --cc="$compiler" --prefix="$prefix" \
        --pkg-config=meowfilm_pkg_config --pkg-config-flags=--static \
        --extra-cflags="-I$xml_prefix/include"
    printf '%s' "$fingerprint" > configure.identity
fi
for feature in NETWORK GPL VERSION3 NONFREE AVDEVICE AVFILTER; do
    grep -qx "#define CONFIG_${feature} 0" config.h || {
        echo "Locked FFmpeg feature boundary changed: ${feature}" >&2; exit 1;
    }
done
if grep -Eq '^#define CONFIG_[A-Z0-9_]+_(DECODER|ENCODER|PROTOCOL|MUXER) 1$' config_components.h; then
    echo 'Private demux build must not contain decoders, encoders, protocols or muxers' >&2
    exit 1
fi
for demux in MOV HLS DASH; do
    grep -qx "#define CONFIG_${demux}_DEMUXER 1" config_components.h || {
        echo "Required controlled demux is missing: ${demux}" >&2; exit 1;
    }
done
# Native MinGW make cannot resolve Git Bash's /c/... prerequisite paths.
# Relative paths work in both make and recipes, without expanding every header
# to an absolute path in Windows' bounded recipe command line.
make_source=$(realpath --relative-to="$build_dir" "$source_dir")
make_arguments=(-f "$make_source/Makefile" "SRC_PATH=$make_source" "SRC_LINK=$make_source" SHELL=sh.exe)
"$make" "${make_arguments[@]}" -j"$jobs"
# The brief INSTALL wrapper duplicates the complete header list in one recipe.
"$make" "${make_arguments[@]}" V=1 install
