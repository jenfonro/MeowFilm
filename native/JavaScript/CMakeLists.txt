cmake_minimum_required(VERSION 3.20)
project(meowfilm_js LANGUAGES C)

# Source and license bytes are pinned by scripts/quickjs-native.json.
# Only the engine is linked: no qjs CLI, libc, module loader or I/O bindings.
add_library(meowfilm_js SHARED meowfilm_js.c
    quickjs/quickjs.c quickjs/dtoa.c quickjs/libregexp.c quickjs/libunicode.c)
target_include_directories(meowfilm_js PRIVATE quickjs)
set_target_properties(meowfilm_js PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED ON
    C_VISIBILITY_PRESET hidden RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>")
if(WIN32)
    set_target_properties(meowfilm_js PROPERTIES PREFIX "")
    if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
        message(FATAL_ERROR "The Windows application requires a 64-bit native engine")
    endif()
    target_compile_definitions(meowfilm_js PRIVATE WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0601
        _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_DEPRECATE)
    if(MSVC)
        set_property(TARGET meowfilm_js PROPERTY MSVC_RUNTIME_LIBRARY
            "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
else()
    target_compile_definitions(meowfilm_js PRIVATE _GNU_SOURCE)
    find_package(Threads REQUIRED)
    target_link_libraries(meowfilm_js PRIVATE m ${CMAKE_DL_LIBS} Threads::Threads)
endif()
