// swift-tools-version: 6.1
import PackageDescription

let libraries = [
    "AetherLibavcodec", "AetherLibavformat", "AetherLibavutil",
    "AetherLibswresample", "AetherLibswscale", "AetherLibavfilter",
    "AetherLibdav1d", "AetherLibzimg", "AetherLibzvbi"
]

let package = Package(
    name: "AetherKernel",
    platforms: [.iOS(.v18), .macOS(.v15)],
    products: [.library(name: "MeowAetherKernel", type: .dynamic, targets: ["MeowAetherKernel"])],
    targets: libraries.map {
        .binaryTarget(name: $0, path: ".cache/binaries/\($0).xcframework")
    } + [
        .binaryTarget(name: "Dovi", path: ".cache/binaries/Dovi.xcframework"),
        .target(
            name: "AetherFFmpegBuild",
            dependencies: libraries.map { .target(name: $0) },
            path: ".cache/AetherFFmpegBuild",
            linkerSettings: [
                .linkedFramework("AudioToolbox"), .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"), .linkedFramework("VideoToolbox"),
                .linkedLibrary("z"), .linkedLibrary("bz2"),
                .linkedLibrary("iconv"), .linkedLibrary("c++")
            ]
        ),
        .target(
            name: "AetherEngine",
            dependencies: ["AetherFFmpegBuild", "Dovi"],
            path: ".cache/AetherEngine",
            resources: [.copy("Resources/silent-atmos.mp4"), .copy("Resources/PrivacyInfo.xcprivacy")],
            linkerSettings: [
                .linkedFramework("AVFoundation"), .linkedFramework("AVKit"),
                .linkedFramework("CoreMedia"), .linkedFramework("CoreVideo"),
                .linkedFramework("VideoToolbox"), .linkedFramework("AudioToolbox")
            ]
        ),
        .target(name: "MeowAetherKernel", dependencies: ["AetherEngine"]),
        .testTarget(name: "MeowAetherKernelTests", dependencies: ["MeowAetherKernel"])
    ]
)
