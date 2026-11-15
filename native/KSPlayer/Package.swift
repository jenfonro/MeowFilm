// swift-tools-version: 5.10
import PackageDescription

let libraries = ["avformat", "avcodec", "avutil", "avfilter", "swresample", "swscale"]
let modules = libraries.map { "Lib" + $0 }
let package = Package(
    name: "KSPlayer",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "KSPlayer", targets: ["KSPlayer"])],
    targets: libraries.map {
        .binaryTarget(name: "KSFF" + $0, path: ".cache/Artifacts/lib" + $0 + ".xcframework")
    } + libraries.map {
        .target(name: "Lib" + $0, dependencies: libraries.map { .byName(name: "KSFF" + $0) },
                path: "Sources/Lib" + $0, publicHeadersPath: "include")
    } + [
        .target(name: "FFmpegKit", dependencies: modules.map { .byName(name: $0) },
                publicHeadersPath: "include",
                linkerSettings: [
                    .linkedLibrary("xml2"), .linkedLibrary("z"), .linkedLibrary("bz2"), .linkedLibrary("iconv"),
                    .linkedFramework("AVFoundation"), .linkedFramework("CoreMedia"), .linkedFramework("CoreVideo"),
                    .linkedFramework("Metal"), .linkedFramework("CoreImage"), .linkedFramework("QuartzCore"),
                    .linkedFramework("VideoToolbox"), .linkedFramework("AudioToolbox"),
                    .linkedFramework("Security"), .linkedFramework("CoreFoundation")
                ]),
        .target(name: "DisplayCriteria", publicHeadersPath: "include"),
        .target(name: "KSPlayer", dependencies: (["FFmpegKit", "DisplayCriteria"] + modules).map {
                    .byName(name: $0)
                }, resources: [.process("Metal/Shaders.metal")]),
        .testTarget(name: "KSPlayerTests", dependencies: ["KSPlayer", "Libavcodec", "Libavutil"])
    ],
    swiftLanguageVersions: [.v5],
    cLanguageStandard: .c11
)
