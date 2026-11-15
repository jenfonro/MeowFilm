#!/usr/bin/env bash
set -euo pipefail

source_dir=$(cygpath -u "$1")
build_dir=$(cygpath -u "$2")
prefix=$(cygpath -u "$3")
compiler=$(cygpath -u "$4")
make=$(cygpath -u "$5")
nasm=$(cygpath -u "$6")
flags_file=$(cygpath -u "$7")
identity=$8
jobs=$9

export LC_ALL=C
export SOURCE_DATE_EPOCH=1786506890
mkdir -p "$build_dir"
cd "$build_dir"
flags=()
while IFS= read -r flag; do flags+=("$flag"); done < "$flags_file"
if [[ ! -f ffbuild/config.mak ]]; then
    bash "$source_dir/configure" "${flags[@]}" --cc="$compiler" --x86asmexe="$nasm" --prefix="$prefix"
    printf '%s' "$identity" > configure.identity
fi
[[ -f configure.identity && "$(cat configure.identity)" == "$identity" ]] || {
    echo 'Audio FFmpeg build directory belongs to different inputs' >&2; exit 1;
}
for feature in GPL VERSION3 NONFREE NETWORK STATIC AVDEVICE SWSCALE; do
    grep -qx "#define CONFIG_${feature} 0" config.h || {
        echo "Audio FFmpeg feature must be disabled: $feature" >&2; exit 1;
    }
done
for feature in SHARED AVUTIL SWRESAMPLE AVCODEC AVFORMAT AVFILTER; do
    grep -qx "#define CONFIG_${feature} 1" config.h || {
        echo "Audio FFmpeg feature must be enabled: $feature" >&2; exit 1;
    }
done
grep -qx '#define HAVE_PTHREADS 0' config.h || {
    echo 'Audio FFmpeg must not use pthreads' >&2; exit 1;
}
grep -qx '#define HAVE_W32THREADS 1' config.h || {
    echo 'Audio FFmpeg requires Windows native threads' >&2; exit 1;
}
if grep -Eq '^#define CONFIG_[A-Z0-9_]+_(ENCODER|MUXER|HWACCEL|INDEV|OUTDEV) 1$' config_components.h; then
    echo 'Audio FFmpeg must not include encoders, muxers, devices or hardware accelerators' >&2
    exit 1
fi
if grep -E '^#define CONFIG_[A-Z0-9_]+_PROTOCOL 1$' config_components.h | grep -vq '^#define CONFIG_FILE_PROTOCOL 1$'; then
    echo 'Only the local file protocol is allowed; network input is managed AVIO' >&2
    exit 1
fi
for decoder in AAC ALAC APE FLAC MP3FLOAT OPUS VORBIS PCM_S16LE; do
    grep -qx "#define CONFIG_${decoder}_DECODER 1" config_components.h || {
        echo "Audio FFmpeg decoder must be enabled: $decoder" >&2; exit 1;
    }
done
for filter in AFORMAT ANULL ARESAMPLE ATEMPO; do
    grep -qx "#define CONFIG_${filter}_FILTER 1" config_components.h || {
        echo "Audio FFmpeg filter must be enabled: $filter" >&2; exit 1;
    }
done
# These public-API endpoints are always registered, not configurable filters.
grep -q 'ff_asrc_abuffer' libavfilter/filter_list.c || {
    echo 'Audio FFmpeg buffer source is not registered' >&2; exit 1;
}
grep -q 'ff_asink_abuffer' libavfilter/filter_list.c || {
    echo 'Audio FFmpeg buffer sink is not registered' >&2; exit 1;
}
# Native MinGW make needs relative source paths, not Git Bash's /e/... paths.
make_source=$(realpath --relative-to="$build_dir" "$source_dir")
arguments=(-f "$make_source/Makefile" "SRC_PATH=$make_source" "SRC_LINK=$make_source" SHELL=sh.exe)
"$make" "${arguments[@]}" -j"$jobs"
# The App consumes shared libraries, not a developer SDK. Installing all headers
# exceeds native Windows make's command limit for libavutil's long source path.
"$make" "${arguments[@]}" V=1 install-libs
