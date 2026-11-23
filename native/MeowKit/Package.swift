// swift-tools-version: 5.10
import PackageDescription

// iOS is the product; macOS is only used for host SwiftPM compile verification.
let package = Package(
    name: "MeowKit",
    platforms: [.iOS("18.0"), .macOS("15.0")],
    products: [
        .library(name: "MeowContracts", targets: ["MeowContracts"]),
        .library(name: "MeowJSON", targets: ["MeowJSON"]),
        .library(name: "MeowDebug", targets: ["MeowDebug"]),
        .library(name: "MeowStorage", targets: ["MeowStorage"]),
        .library(name: "MeowAccounts", targets: ["MeowAccounts"]),
        .library(name: "MeowPlayback", targets: ["MeowPlayback"]),
        .library(name: "MeowAudio", targets: ["MeowAudio"]),
        .library(name: "MeowPlaybackVideo", targets: ["MeowPlaybackVideo"]),
        .library(name: "MeowUI", targets: ["MeowUI"]),
        .library(name: "MeowRuntime", targets: ["MeowRuntime"]),
        .library(name: "MeowMusicFeature", targets: ["MeowMusicFeature"]),
        .library(name: "MeowMusicPlatform", targets: ["MeowMusicPlatform"]),
        .library(name: "MeowMusicNativeService", targets: ["MeowMusicNativeService"]),
        .library(name: "MeowFilmFeature", targets: ["MeowFilmFeature"]),
        .library(name: "MeowNovelFeature", targets: ["MeowNovelFeature"])
    ],
    dependencies: [
        .package(path: "../VideoKernel"),
        .package(path: "../AetherKernel"),
        .package(path: "../AudioKernel")
    ],
    targets: [
        .binaryTarget(name: "MeowSQLite", path: "../SQLite/.cache/Artifacts/MeowSQLite.xcframework"),
        .target(name: "MeowContracts", dependencies: []),
        .target(name: "MeowJSON", dependencies: []),
        .target(name: "MeowDebug", dependencies: []),
        .target(name: "MeowStorage", dependencies: ["MeowContracts", "MeowDebug", "MeowSQLite"],
                resources: [
                    .copy("Resources/storage-data-schema.sql"),
                    .copy("Resources/storage-cache-schema.sql")
                ]),
        .target(name: "MeowAccounts", dependencies: ["MeowContracts"]),
        .target(name: "MeowAudio", dependencies: ["MeowDebug",
            .product(name: "CMeowAudioKernel", package: "audiokernel")]),
        .target(name: "MeowPlayback", dependencies: ["MeowContracts", "MeowDebug", "MeowAudio", "MeowStorage"]),
        .target(name: "MeowPlaybackVideo", dependencies: ["MeowContracts", "MeowDebug", "MeowPlayback",
            .product(name: "MeowVideoKernel", package: "videokernel"),
            .product(name: "MeowAetherKernel", package: "aetherkernel")]),
        .target(name: "MeowUI", dependencies: ["MeowDebug", "MeowStorage"]),
        .target(name: "MeowRuntime", dependencies: ["MeowDebug", "MeowJSON", "MeowStorage"]),
        .target(name: "MeowMusicFeature", dependencies: ["MeowContracts", "MeowDebug", "MeowStorage", "MeowPlayback", "MeowUI"]),
        .target(name: "MeowMusicPlatform", dependencies: ["MeowMusicFeature", "MeowAudio", "MeowContracts", "MeowDebug", "MeowJSON", "MeowAccounts", "MeowStorage", "MeowRuntime", "MeowUI"]),
        .target(name: "MeowMusicNativeService", dependencies: ["MeowContracts", "MeowDebug", "MeowJSON", "MeowMusicFeature", "MeowMusicPlatform", "MeowRuntime", "MeowStorage"]),
        .target(name: "MeowFilmFeature", dependencies: ["MeowContracts", "MeowDebug", "MeowUI", "MeowJSON", "MeowStorage", "MeowRuntime"]),
        .target(name: "MeowNovelFeature", dependencies: ["MeowDebug", "MeowUI", "MeowStorage", "MeowRuntime"])
    ],
    swiftLanguageVersions: [.v5]
)
