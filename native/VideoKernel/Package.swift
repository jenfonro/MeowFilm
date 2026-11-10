// swift-tools-version: 5.10
import PackageDescription

let libraries = ["avformat", "avcodec", "avutil", "swresample", "swscale"]
let package = Package(
    name: "VideoKernel",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "MeowVideoKernel", targets: ["MeowVideoKernel"])],
    targets: libraries.map {
        .binaryTarget(name: "MeowFF" + $0, path: ".cache/Artifacts/lib" + $0 + ".xcframework")
    } + [
        .target(name: "CMeowVideoKernel", dependencies: libraries.map { .byName(name: "MeowFF" + $0) },
                path: "Sources/CMeowVideoKernel", publicHeadersPath: "include",
                linkerSettings: [
                    .linkedLibrary("xml2"), .linkedLibrary("z"), .linkedLibrary("bz2"), .linkedLibrary("iconv"),
                    .linkedFramework("AVFoundation"), .linkedFramework("CoreMedia"), .linkedFramework("CoreVideo"),
                    .linkedFramework("Metal"), .linkedFramework("CoreImage"), .linkedFramework("QuartzCore"),
                    .linkedFramework("VideoToolbox"), .linkedFramework("AudioToolbox"),
                    .linkedFramework("Security"), .linkedFramework("CoreFoundation")
                ]),
        .target(name: "MeowVideoKernel", dependencies: ["CMeowVideoKernel"]),
        .testTarget(name: "VideoKernelTests", dependencies: ["MeowVideoKernel", "CMeowVideoKernel"],
                    resources: [.copy("Fixtures")])
    ],
    swiftLanguageVersions: [.v5],
    cLanguageStandard: .c11
)
