#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/build-film-kernel-common.sh"

mkdir -p "$build_dir" "$prefix"
cmake_build zlib "-DZLIB_SOURCE=$(cygpath -m "$source_dir/zlib")"
cmake_build bzip2 "-DBZIP2_SOURCE=$(cygpath -m "$source_dir/bzip2")"
cmake_build xz -DXZ_NLS=OFF -DXZ_TOOL_XZ=OFF -DXZ_TOOL_XZDEC=OFF \
    -DXZ_TOOL_LZMADEC=OFF -DXZ_TOOL_LZMAINFO=OFF -DXZ_DOC=OFF
cmake_build xml -DLIBXML2_WITH_PROGRAMS=OFF -DLIBXML2_WITH_TESTS=OFF \
    -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_ICONV=OFF -DLIBXML2_WITH_ZLIB=OFF \
    -DLIBXML2_WITH_LZMA=OFF -DLIBXML2_WITH_ICU=OFF -DLIBXML2_WITH_HTTP=OFF \
    -DLIBXML2_WITH_MODULES=OFF -DLIBXML2_WITH_CATALOG=OFF
cmake_build glslang -DBUILD_EXTERNAL=OFF -DENABLE_OPT=OFF -DENABLE_SPIRV=ON \
    -DENABLE_GLSLANG_BINARIES=OFF -DENABLE_SPVREMAPPER=OFF -DGLSLANG_TESTS=OFF \
    -DGLSLANG_ENABLE_INSTALL=ON -DENABLE_PCH=OFF
cmake_build spirv-cross -DSPIRV_CROSS_SHARED=OFF -DSPIRV_CROSS_STATIC=ON \
    -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_ENABLE_GLSL=ON \
    -DSPIRV_CROSS_ENABLE_HLSL=ON -DSPIRV_CROSS_ENABLE_C_API=ON \
    -DSPIRV_CROSS_ENABLE_MSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF \
    -DSPIRV_CROSS_ENABLE_REFLECT=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF
# Upstream omits these dependencies from the static C API .pc, despite linking them in CMake.
spirv_pc="$prefix/lib/pkgconfig/spirv-cross-c.pc"
sed -i '/^Libs.private:/d' "$spirv_pc"
printf '%s\n' 'Libs.private: -lspirv-cross-hlsl -lspirv-cross-glsl -lspirv-cross-core' >> "$spirv_pc"

meson_build dav1d -Denable_tools=false -Denable_examples=false -Denable_tests=false -Denable_docs=false
meson_build freetype -Dharfbuzz=disabled -Dzlib=system -Dbzip2=enabled \
    -Dbrotli=disabled -Dpng=disabled -Dtests=disabled
meson_build fribidi -Ddocs=false -Dbin=false -Dtests=false
meson_build harfbuzz -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dchafa=disabled \
    -Dicu=disabled -Dgraphite2=disabled -Dfreetype=disabled -Dintrospection=disabled \
    -Ddocs=disabled -Dtests=disabled -Dutilities=disabled
cmake_build unibreak "-DUNIBREAK_SOURCE=$(cygpath -m "$source_dir/unibreak")"
meson_build ass -Ddirectwrite=enabled -Dfontconfig=disabled -Dcoretext=disabled \
    -Dlibunibreak=enabled -Dasm=enabled -Dtest=disabled -Dcompare=disabled \
    -Dprofile=disabled -Dfuzz=disabled -Dcheckasm=disabled
meson_build placebo -Dd3d11=enabled -Ddovi=enabled -Dglslang=enabled \
    "-Dvulkan-sdk=$(cygpath -m "$prefix")" \
    -Dshaderc=disabled -Dlibdovi=disabled -Dvulkan=disabled -Dopengl=disabled \
    -Dgl-proc-addr=disabled -Dlcms=disabled -Dunwind=disabled -Dxxhash=disabled \
    -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false
# libplacebo's MinGW UCRT shim is generated but not installed upstream.
cp "$build_dir/placebo/src/ucrt_math.lib" "$prefix/lib/ucrt_math.lib"
MFK_PC_PREFIX=$(cygpath -m "$prefix") MFK_PC_BUILD=$(cygpath -m "$build_dir/placebo") \
    "$MFK_PYTHON" -B -c \
    'import os,pathlib; p=pathlib.Path(os.environ["MFK_PC_PREFIX"])/"lib/pkgconfig/libplacebo.pc"; s=p.read_text(); old=os.environ["MFK_PC_BUILD"]; s=s.replace(old+"/src/ucrt_math.lib", os.environ["MFK_PC_PREFIX"]+"/lib/ucrt_math.lib"); p.write_text(s, newline="\n")'
for component in D3D11 DOVI GLSLANG; do
    grep -Eq "^#define PL_HAVE_${component}([[:space:]]+1)?$" "$prefix/include/libplacebo/config.h"
done

pkg_config --static --exists libplacebo libass libunibreak dav1d libxml-2.0 zlib bzip2 liblzma
