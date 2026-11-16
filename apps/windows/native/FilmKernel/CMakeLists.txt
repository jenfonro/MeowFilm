cmake_minimum_required(VERSION 3.24)
project(MeowFilmFilmKernel LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "The film kernel targets Windows x64")
endif()
foreach(input DEPS_PREFIX BUILD_IDENTITY)
    if(NOT DEFINED ${input})
        message(FATAL_ERROR "Missing locked film kernel input: ${input}")
    endif()
endforeach()
set(ENV{PKG_CONFIG_PATH} "${DEPS_PREFIX}/lib/pkgconfig")
find_package(PkgConfig REQUIRED)
# Every dependency is deliberately static. Include .pc private requirements too.
set(PKG_CONFIG_ARGN --static)
pkg_check_modules(MEDIA REQUIRED IMPORTED_TARGET
    libavformat libavcodec libavutil libavfilter libswresample
    libplacebo>=7.387.0 libass)

add_library(meowfilm_film_kernel SHARED
    film_kernel.cpp film_io.c film_audio.cpp film_dovi.cpp film_renderer.cpp film_libav.c)
set_target_properties(meowfilm_film_kernel PROPERTIES
    PREFIX "" OUTPUT_NAME "meowfilm_film_kernel"
    C_STANDARD 11 C_STANDARD_REQUIRED YES CXX_STANDARD 17 CXX_STANDARD_REQUIRED YES)
target_compile_definitions(meowfilm_film_kernel PRIVATE
    _WIN32_WINNT=0x0A00 WINVER=0x0A00 NOMINMAX
    MFK_BUILD_IDENTITY="${BUILD_IDENTITY}")
target_compile_options(meowfilm_film_kernel PRIVATE -Wall -Wextra
    -Wno-unused-parameter -Wno-missing-field-initializers)
target_link_options(meowfilm_film_kernel PRIVATE -static -Wl,--exclude-all-symbols -Wl,-Map,film-kernel.map)
target_link_libraries(meowfilm_film_kernel PRIVATE PkgConfig::MEDIA
    d3d11 dxgi dxguid d3dcompiler ole32 uuid ksuser avrt user32 gdi32 version
    bcrypt advapi32 ws2_32 shlwapi mfplat mfuuid)

add_executable(film_audio_tests EXCLUDE_FROM_ALL film_audio_tests.cpp)
add_executable(film_dovi_tests EXCLUDE_FROM_ALL film_dovi_tests.cpp film_dovi.cpp)
add_executable(film_renderer_tests EXCLUDE_FROM_ALL film_renderer_tests.cpp film_libav.c)
add_executable(film_io_tests EXCLUDE_FROM_ALL film_io_tests.c)
foreach(test film_audio_tests film_dovi_tests film_renderer_tests)
    target_compile_features(${test} PRIVATE cxx_std_17)
    target_compile_definitions(${test} PRIVATE NOMINMAX _WIN32_WINNT=0x0A00 WINVER=0x0A00)
    target_link_options(${test} PRIVATE -static)
    target_link_libraries(${test} PRIVATE PkgConfig::MEDIA ole32 uuid bcrypt ws2_32 mfplat mfuuid)
endforeach()
target_link_libraries(film_audio_tests PRIVATE ksuser avrt)
target_link_libraries(film_renderer_tests PRIVATE d3d11 dxgi dxguid d3dcompiler user32 gdi32 version advapi32 shlwapi)
target_compile_features(film_io_tests PRIVATE c_std_11)
target_link_options(film_io_tests PRIVATE -static)
target_link_libraries(film_io_tests PRIVATE PkgConfig::MEDIA ole32 uuid bcrypt ws2_32 mfplat mfuuid)
