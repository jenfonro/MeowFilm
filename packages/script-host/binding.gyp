{
  "variables": {
    "runtime_native": "../../tmp/build/runtime-native",
    "quickjs_source": "../../native/JavaScript/quickjs",
    "openssl_library%": "../../tmp/build/runtime-native/openssl-build/libcrypto.a"
  },
  "target_defaults": {
    "defines": ["NAPI_VERSION=8"],
    "cflags": ["-O2", "-fPIC", "-fvisibility=hidden"],
    "cflags_c": ["-std=c11", "-D_GNU_SOURCE"],
    "cflags_cc": ["-std=c++17"],
    "conditions": [
      ["OS=='win'", {
        "defines": ["WIN32_LEAN_AND_MEAN", "NOMINMAX"],
        "msvs_settings": {
          "VCCLCompilerTool": {
            "ExceptionHandling": 1,
            "AdditionalOptions!": ["-std:c++20"],
            "LanguageStandard": "stdcpp17",
            "LanguageStandard_C": "stdc11",
            "AdditionalOptions": ["/utf-8"]
          }
        }
      }],
      ["OS=='mac'", {
        "xcode_settings": {
          "CLANG_CXX_LANGUAGE_STANDARD": "c++17",
          "GCC_ENABLE_CPP_EXCEPTIONS": "YES",
          "GCC_SYMBOLS_PRIVATE_EXTERN": "YES"
        }
      }]
    ]
  },
  "targets": [
    {
      "target_name": "quickjs",
      "type": "static_library",
      "defines": ["QUICKJS_NG_BUILD"],
      "include_dirs": ["<(quickjs_source)"],
      "sources": [
        "<(quickjs_source)/quickjs.c",
        "<(quickjs_source)/dtoa.c",
        "<(quickjs_source)/libregexp.c",
        "<(quickjs_source)/libunicode.c"
      ]
    },
    {
      "target_name": "zlib",
      "type": "static_library",
      "include_dirs": ["<(runtime_native)/zlib"],
      "sources": [
        "<(runtime_native)/zlib/adler32.c",
        "<(runtime_native)/zlib/compress.c",
        "<(runtime_native)/zlib/crc32.c",
        "<(runtime_native)/zlib/deflate.c",
        "<(runtime_native)/zlib/inffast.c",
        "<(runtime_native)/zlib/inflate.c",
        "<(runtime_native)/zlib/inftrees.c",
        "<(runtime_native)/zlib/trees.c",
        "<(runtime_native)/zlib/uncompr.c",
        "<(runtime_native)/zlib/zutil.c"
      ]
    },
    {
      "target_name": "script_host_native",
      "dependencies": ["quickjs", "zlib"],
      "include_dirs+": [
        "<(runtime_native)/openssl-build/include",
        "<(runtime_native)/openssl/include"
      ],
      "include_dirs": [
        "<(quickjs_source)",
        "<(runtime_native)/zlib"
      ],
      "sources": ["native/addon.cc", "native/engine.cc", "native/value.cc", "native/algorithms.cc"],
      "cflags_cc!": ["-fno-exceptions", "-fno-rtti"],
      "libraries": ["<(openssl_library)"],
      "conditions": [
        ["OS=='linux'", {
          "libraries": ["-lpthread", "-ldl", "-lm"],
          "ldflags": ["-Wl,-Bsymbolic", "-Wl,--exclude-libs,ALL"]
        }],
        ["OS=='mac'", {
          "xcode_settings": {
            "GCC_ENABLE_CPP_EXCEPTIONS": "YES",
            "GCC_ENABLE_CPP_RTTI": "YES"
          }
        }],
        ["OS=='win'", {
          "defines!": ["_HAS_EXCEPTIONS=0"],
          "msvs_settings": {
            "VCCLCompilerTool": { "RuntimeTypeInfo": "true" }
          },
          "configurations": {
            "Release": {
              "msvs_settings": {
                "VCCLCompilerTool": { "RuntimeTypeInfo": "true" }
              }
            }
          },
          "libraries": ["ws2_32.lib", "crypt32.lib", "advapi32.lib", "user32.lib", "bcrypt.lib"]
        }]
      ]
    }
  ]
}
