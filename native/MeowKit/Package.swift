// swift-tools-version: 5.10
import PackageDescription

// iOS is the product; macOS is only the native XCTest executor.
let package = Package(
    name: "MeowKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "MeowContracts", targets: ["MeowContracts"]),
        .library(name: "MeowJSON", targets: ["MeowJSON"]),
        .library(name: "MeowStorage", targets: ["MeowStorage"]),
        .library(name: "MeowAccounts", targets: ["MeowAccounts"]),
        .library(name: "MeowPlayback", targets: ["MeowPlayback"]),
        .library(name: "MeowUI", targets: ["MeowUI"]),
        .library(name: "MeowMusicFeature", targets: ["MeowMusicFeature"]),
        .library(name: "MeowMusicPlatform", targets: ["MeowMusicPlatform"]),
        .library(name: "MeowMusicNativeService", targets: ["MeowMusicNativeService"])
    ],
    targets: [
        .target(name: "MeowContracts", dependencies: []),
        .target(name: "MeowJSON", dependencies: []),
        .target(name: "MeowStorage", dependencies: ["MeowContracts"]),
        .target(name: "MeowAccounts", dependencies: ["MeowContracts"]),
        .target(name: "MeowPlayback", dependencies: ["MeowContracts"]),
        .target(name: "MeowUI", dependencies: []),
        .target(name: "MeowMusicFeature", dependencies: ["MeowContracts", "MeowStorage", "MeowPlayback", "MeowUI"]),
        .target(name: "MeowMusicPlatform", dependencies: ["MeowMusicFeature", "MeowContracts", "MeowJSON", "MeowAccounts", "MeowStorage"], linkerSettings: [.linkedLibrary("z")]),
        .target(name: "MeowMusicNativeService", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowStorage"], linkerSettings: [.linkedLibrary("z")]),
        .testTarget(name: "MeowJSONTests", dependencies: ["MeowJSON"]),
        .testTarget(name: "MeowAccountTests", dependencies: ["MeowAccounts", "MeowContracts"]),
        .testTarget(name: "MeowCoreTests", dependencies: ["MeowContracts", "MeowStorage"]),
        .testTarget(name: "MeowMusicFeatureTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowPlayback", "MeowStorage", "MeowUI"]),
        .testTarget(name: "MeowMusicNativeServiceTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowMusicNativeService", "MeowMusicPlatform", "MeowStorage"]),
        .testTarget(name: "MeowMusicPlatformTests", dependencies: ["MeowAccounts", "MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowMusicPlatform", "MeowStorage"]),
        .testTarget(name: "MeowPlaybackTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowPlayback"]),
        .testTarget(name: "MeowUITests", dependencies: ["MeowUI"])
    ],
    swiftLanguageVersions: [.v5]
)
