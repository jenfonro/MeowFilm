cmake_minimum_required(VERSION 3.20)
project(MeowFilmVideoKernel LANGUAGES C)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "The locked VLC plugin SDK is Windows x64 only")
endif()
foreach(input VLC_SDK FFMPEG_PREFIX XML_PREFIX WINPTHREAD_LIBRARY BUILD_IDENTITY)
    if(NOT DEFINED ${input})
        message(FATAL_ERROR "Missing locked build input: ${input}")
    endif()
endforeach()

add_library(meowfilm_ffmpeg_plugin MODULE video_kernel.c video_io.c video_tracks.c)
set_target_properties(meowfilm_ffmpeg_plugin PROPERTIES
    PREFIX "lib" OUTPUT_NAME "meowfilm_ffmpeg_plugin" C_STANDARD 11 C_STANDARD_REQUIRED YES)
target_include_directories(meowfilm_ffmpeg_plugin PRIVATE
    "${VLC_SDK}/include" "${VLC_SDK}/include/vlc/plugins"
    "${FFMPEG_PREFIX}/include" "${XML_PREFIX}/include/libxml2")
target_compile_definitions(meowfilm_ffmpeg_plugin PRIVATE
    __PLUGIN__ MODULE_NAME=meowfilm_ffmpeg MODULE_STRING="meowfilm_ffmpeg"
    _FILE_OFFSET_BITS=64 _REENTRANT _THREAD_SAFE _WIN32_WINNT=0x0601 LIBXML_STATIC
    MVK_BUILD_IDENTITY="${BUILD_IDENTITY}")
if(MINGW)
    target_compile_options(meowfilm_ffmpeg_plugin PRIVATE -Wall -Wextra -Werror
        -Wno-unused-parameter -Wno-missing-field-initializers)
    target_link_options(meowfilm_ffmpeg_plugin PRIVATE
        -static-libgcc -Wl,--exclude-libs,ALL -Wl,-Map,video-kernel.map)
endif()
target_link_libraries(meowfilm_ffmpeg_plugin PRIVATE
    "${VLC_SDK}/lib/libvlccore.lib"
    "${FFMPEG_PREFIX}/lib/libavformat.a" "${FFMPEG_PREFIX}/lib/libavcodec.a"
    "${FFMPEG_PREFIX}/lib/libavutil.a" "${XML_PREFIX}/lib/libxml2.a"
    bcrypt advapi32 user32 ole32 ws2_32 shlwapi m "${WINPTHREAD_LIBRARY}")
