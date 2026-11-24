// swift-tools-version: 5.10
import PackageDescription

let libraries = ["avformat", "avcodec", "avutil", "swresample", "swscale"]

let package = Package(
    name: "VideoKernel",
    platforms: [.iOS("18.0"), .macOS("15.0")],
    products: [
        .library(name: "MeowVideoKernel", targets: ["MeowVideoKernel"])
    ],
    targets: libraries.map {
        .binaryTarget(name: "MeowFF" + $0, path: ".cache/Artifacts/lib" + $0 + ".xcframework")
    } + [
        .target(
            name: "CMeowVideoFFmpeg",
            dependencies: libraries.map { .byName(name: "MeowFF" + $0) },
            publicHeadersPath: "include",
            linkerSettings: [
                .linkedLibrary("xml2"),
                .linkedLibrary("z"),
                .linkedLibrary("bz2"),
                .linkedLibrary("iconv"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("VideoToolbox"),
                .linkedFramework("AudioToolbox"),
                .linkedFramework("QuartzCore"),
                .linkedFramework("Security"),
                .linkedFramework("CoreFoundation")
            ]
        ),
        .target(
            name: "MeowVideoKernel",
            dependencies: ["CMeowVideoFFmpeg"]
        )
    ],
    swiftLanguageVersions: [.v5],
    cLanguageStandard: .c11
)
