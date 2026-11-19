// swift-tools-version: 5.10
import PackageDescription

// iOS is the product; macOS is only the native XCTest executor.
let package = Package(
    name: "MeowKit",
    platforms: [.iOS("18.0"), .macOS("15.0")],
    products: [
        .library(name: "MeowContracts", targets: ["MeowContracts"]),
        .library(name: "MeowJSON", targets: ["MeowJSON"]),
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
        .target(name: "MeowStorage", dependencies: ["MeowContracts", "MeowSQLite"],
                resources: [.copy("Resources/storage-schema.sql")]),
        .target(name: "MeowAccounts", dependencies: ["MeowContracts"]),
        .target(name: "MeowAudio", dependencies: [
            .product(name: "CMeowAudioKernel", package: "audiokernel")]),
        .target(name: "MeowPlayback", dependencies: ["MeowContracts", "MeowAudio", "MeowStorage"]),
        .target(name: "MeowPlaybackVideo", dependencies: ["MeowContracts", "MeowPlayback",
            .product(name: "MeowVideoKernel", package: "videokernel"),
            .product(name: "MeowAetherKernel", package: "aetherkernel")]),
        .target(name: "MeowUI", dependencies: ["MeowStorage"]),
        .target(name: "MeowRuntime", dependencies: ["MeowJSON", "MeowStorage"]),
        .target(name: "MeowMusicFeature", dependencies: ["MeowContracts", "MeowStorage", "MeowPlayback", "MeowUI"]),
        .target(name: "MeowMusicPlatform", dependencies: ["MeowMusicFeature", "MeowAudio", "MeowContracts", "MeowJSON", "MeowAccounts", "MeowStorage", "MeowRuntime", "MeowUI"]),
        .target(name: "MeowMusicNativeService", dependencies: ["MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowMusicPlatform", "MeowRuntime", "MeowStorage"]),
        .target(name: "MeowFilmFeature", dependencies: ["MeowContracts", "MeowUI", "MeowJSON", "MeowStorage", "MeowRuntime"]),
        .target(name: "MeowNovelFeature", dependencies: ["MeowUI", "MeowStorage", "MeowRuntime"]),
        .target(name: "MeowMusicTestSupport", dependencies: ["MeowMusicFeature", "MeowMusicPlatform", "MeowRuntime"]),
        .target(name: "MeowStorageTestSupport", dependencies: ["MeowStorage"]),
        .testTarget(name: "MeowNovelFeatureTests", dependencies: ["MeowNovelFeature", "MeowStorage", "MeowRuntime", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowJSONTests", dependencies: ["MeowJSON"]),
        .testTarget(name: "MeowAccountTests", dependencies: ["MeowAccounts", "MeowContracts"]),
        .testTarget(name: "MeowCoreTests", dependencies: ["MeowContracts", "MeowStorage", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowMusicFeatureTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowPlayback", "MeowStorage", "MeowUI", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowMusicNativeServiceTests", dependencies: ["MeowAccounts", "MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowMusicNativeService", "MeowMusicPlatform", "MeowMusicTestSupport", "MeowPlayback", "MeowRuntime", "MeowStorage", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowMusicPlatformTests", dependencies: ["MeowAccounts", "MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowMusicPlatform", "MeowMusicTestSupport", "MeowRuntime", "MeowStorage", "MeowUI", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowPlaybackTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowPlayback", "MeowAudio", "MeowPlaybackVideo", "MeowStorage", "MeowStorageTestSupport",
            .product(name: "MeowVideoKernel", package: "videokernel")],
                    resources: [.copy("Video/Fixtures")]),
        .testTarget(name: "MeowRuntimeTests", dependencies: ["MeowJSON", "MeowRuntime", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowUITests", dependencies: ["MeowUI", "MeowStorage", "MeowStorageTestSupport"]),
        .testTarget(name: "MeowFilmFeatureTests", dependencies: ["MeowContracts", "MeowFilmFeature", "MeowStorage", "MeowJSON", "MeowRuntime", "MeowStorageTestSupport"])
    ],
    swiftLanguageVersions: [.v5]
)
