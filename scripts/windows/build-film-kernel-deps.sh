#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/build-film-kernel-common.sh"

# configure creates POSIX temporary paths; Git Bash must translate them for clang.
unset MSYS2_ARG_CONV_EXCL
mkdir -p "$build_dir/ffmpeg"
cd "$build_dir/ffmpeg"
grep -qx '#define FF_MEOWFILM_DOVI 1' "$source_dir/ffmpeg/libavcodec/packet.h"
ffmpeg_flags=(
    --target-os=mingw32 --arch=x86_64
    --disable-autodetect --disable-shared --enable-static --disable-programs --disable-doc --disable-debug
    --disable-encoders --disable-muxers --disable-avdevice --disable-network --disable-protocols
    --disable-gpl --disable-nonfree --disable-pthreads --enable-w32threads
    --enable-avcodec --enable-avformat --enable-avutil --enable-avfilter --enable-swresample --enable-swscale
    --enable-decoders --enable-demuxers --enable-parsers --enable-bsfs
    --enable-libxml2 --enable-libdav1d --enable-zlib --enable-bzlib --enable-lzma
    --enable-d3d11va --disable-dxva2 --disable-d3d12va
    --enable-hwaccel=h264_d3d11va,hevc_d3d11va,av1_d3d11va,vp9_d3d11va,mpeg2_d3d11va,vc1_d3d11va,wmv3_d3d11va
    --enable-hwaccel=h264_d3d11va2,hevc_d3d11va2,av1_d3d11va2,vp9_d3d11va2,mpeg2_d3d11va2,vc1_d3d11va2,wmv3_d3d11va2
    --disable-filters --enable-filter=aformat,aresample,atempo --enable-x86asm
    "--cc=$MFK_CC" "--cxx=$MFK_CXX" "--ar=$MFK_AR" "--ranlib=$MFK_RANLIB"
    "--nm=$MFK_NM" "--windres=$MFK_WINDRES" "--strip=$MFK_STRIP" "--x86asmexe=$MFK_NASM"
    "--prefix=$(cygpath -m "$prefix")" --pkg-config=pkg_config --pkg-config-flags=--static
    "--extra-cflags=-I$(cygpath -m "$prefix")/include"
    "--extra-ldflags=-L$(cygpath -m "$prefix")/lib -static"
)
[[ "$configuration" != Debug ]] || ffmpeg_flags+=(--enable-debug=3 --disable-optimizations)
if [[ ! -f ffbuild/config.mak ]]; then
    bash "$source_dir/ffmpeg/configure" "${ffmpeg_flags[@]}"
fi
for feature in NETWORK GPL NONFREE AVDEVICE; do
    grep -qx "#define CONFIG_${feature} 0" config.h || { echo "Unexpected FFmpeg $feature" >&2; exit 1; }
done
grep -qx '#define CONFIG_D3D11VA 1' config.h
for feature in AVFILTER SWRESAMPLE SWSCALE LIBXML2 LIBDAV1D ZLIB BZLIB LZMA DOVI_RPUDEC; do
    grep -qx "#define CONFIG_${feature} 1" config.h
done
for component in HEVC_DECODER H264_DECODER LIBDAV1D_DECODER AAC_DECODER ASS_DECODER SSA_DECODER \
    SUBRIP_DECODER WEBVTT_DECODER DVDSUB_DECODER DVBSUB_DECODER PGSSUB_DECODER \
    HEVC_D3D11VA_HWACCEL HEVC_D3D11VA2_HWACCEL MOV_DEMUXER HLS_DEMUXER DASH_DEMUXER \
    AFORMAT_FILTER ARESAMPLE_FILTER ATEMPO_FILTER; do
    grep -qx "#define CONFIG_${component} 1" config_components.h || {
        echo "Required FFmpeg component missing: $component" >&2; exit 1;
    }
done
if grep -Eq '^#define CONFIG_[A-Z0-9_]+_(PROTOCOL|ENCODER|MUXER) 1$' config_components.h; then
    echo 'FFmpeg includes forbidden protocol/encoder/muxer' >&2; exit 1
fi
for filter in asrc_abuffer asink_abuffer; do
    grep -q "&ff_${filter}," libavfilter/filter_list.c
done
# A native Windows make cannot read /e/... prerequisites; relative SRC_PATH works in both runtimes.
make_source=$(realpath --relative-to="$build_dir/ffmpeg" "$source_dir/ffmpeg")
make_args=(-f "$make_source/Makefile" "SRC_PATH=$make_source" "SRC_LINK=$make_source" SHELL=sh.exe)
"$MFK_MAKE" "${make_args[@]}" -j"$jobs"
"$MFK_MAKE" "${make_args[@]}" V=1 install
grep -qx '#define FF_MEOWFILM_DOVI 1' "$prefix/include/libavcodec/packet.h"
mkdir -p "$prefix/share/meowfilm/ffmpeg"
cp config.h config_components.h "$prefix/share/meowfilm/ffmpeg/"
pkg_config --static --exists libavformat libavcodec libavutil libavfilter libswresample libswscale libplacebo libass
