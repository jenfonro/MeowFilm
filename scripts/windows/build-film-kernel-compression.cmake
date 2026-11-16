cmake_minimum_required(VERSION 3.24)
project(FilmKernelCompression C)
if(ZLIB_SOURCE)
    set(sources adler32 compress crc32 deflate gzclose gzlib gzread gzwrite
        inflate infback inftrees inffast trees uncompr zutil)
    list(TRANSFORM sources PREPEND "${ZLIB_SOURCE}/")
    list(TRANSFORM sources APPEND ".c")
    add_library(z STATIC ${sources})
    target_include_directories(z PRIVATE "${ZLIB_SOURCE}")
    install(TARGETS z ARCHIVE DESTINATION lib)
    install(FILES "${ZLIB_SOURCE}/zlib.h" "${ZLIB_SOURCE}/zconf.h" DESTINATION include)
    file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/zlib.pc"
        "prefix=${CMAKE_INSTALL_PREFIX}\nlibdir=\${prefix}/lib\nincludedir=\${prefix}/include\n\nName: zlib\nDescription: zlib compression library\nVersion: 1.3.1\nLibs: -L\${libdir} -lz\nCflags: -I\${includedir}\n")
    install(FILES "${CMAKE_CURRENT_BINARY_DIR}/zlib.pc" DESTINATION lib/pkgconfig)
    return()
endif()
add_library(bz2 STATIC
    "${BZIP2_SOURCE}/blocksort.c" "${BZIP2_SOURCE}/huffman.c"
    "${BZIP2_SOURCE}/crctable.c" "${BZIP2_SOURCE}/randtable.c"
    "${BZIP2_SOURCE}/compress.c" "${BZIP2_SOURCE}/decompress.c"
    "${BZIP2_SOURCE}/bzlib.c")
target_include_directories(bz2 PRIVATE "${BZIP2_SOURCE}")
install(TARGETS bz2 ARCHIVE DESTINATION lib)
install(FILES "${BZIP2_SOURCE}/bzlib.h" DESTINATION include)
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/bzip2.pc"
    "prefix=${CMAKE_INSTALL_PREFIX}\nlibdir=\${prefix}/lib\nincludedir=\${prefix}/include\n\nName: bzip2\nDescription: bzip2 compression library\nVersion: 1.0.8\nLibs: -L\${libdir} -lbz2\nCflags: -I\${includedir}\n")
install(FILES "${CMAKE_CURRENT_BINARY_DIR}/bzip2.pc" DESTINATION lib/pkgconfig)
