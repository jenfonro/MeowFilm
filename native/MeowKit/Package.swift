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
        .library(name: "MeowRuntime", targets: ["MeowRuntime"]),
        .library(name: "MeowMusicFeature", targets: ["MeowMusicFeature"]),
        .library(name: "MeowMusicPlatform", targets: ["MeowMusicPlatform"]),
        .library(name: "MeowMusicNativeService", targets: ["MeowMusicNativeService"]),
        .library(name: "MeowFilmFeature", targets: ["MeowFilmFeature"])
    ],
    dependencies: [
        .package(path: "../JavaScript"),
        .package(path: "../VideoKernel"),
        .package(path: "../../packages/film-rules"),
        .package(path: "../../packages/film-search")
    ],
    targets: [
        .target(name: "MeowContracts", dependencies: []),
        .target(name: "MeowJSON", dependencies: []),
        .target(name: "MeowStorage", dependencies: ["MeowContracts"]),
        .target(name: "MeowAccounts", dependencies: ["MeowContracts"]),
        .target(name: "MeowPlayback", dependencies: ["MeowContracts",
            .product(name: "MeowVideoKernel", package: "videokernel")]),
        .target(name: "MeowUI", dependencies: []),
        .target(name: "MeowRuntime", dependencies: []),
        .target(name: "MeowMusicFeature", dependencies: ["MeowContracts", "MeowStorage", "MeowPlayback", "MeowUI"]),
        .target(name: "MeowMusicPlatform", dependencies: ["MeowMusicFeature", "MeowContracts", "MeowJSON", "MeowAccounts", "MeowStorage"], linkerSettings: [.linkedLibrary("z")]),
        .target(name: "MeowMusicNativeService", dependencies: ["MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowRuntime", "MeowStorage"], linkerSettings: [.linkedLibrary("z")]),
        .target(name: "MeowFilmFeature", dependencies: ["MeowUI", "MeowJSON", "MeowStorage", "MeowRuntime",
            .product(name: "MeowJavaScript", package: "javascript"),
            .product(name: "FilmRuleProgram", package: "film-rules"),
            .product(name: "FilmSearchProgram", package: "film-search")]),
        .testTarget(name: "MeowJSONTests", dependencies: ["MeowJSON"]),
        .testTarget(name: "MeowAccountTests", dependencies: ["MeowAccounts", "MeowContracts"]),
        .testTarget(name: "MeowCoreTests", dependencies: ["MeowContracts", "MeowStorage"]),
        .testTarget(name: "MeowMusicFeatureTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowPlayback", "MeowStorage", "MeowUI"]),
        .testTarget(name: "MeowMusicNativeServiceTests", dependencies: ["MeowAccounts", "MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowMusicNativeService", "MeowMusicPlatform", "MeowPlayback", "MeowRuntime", "MeowStorage"]),
        .testTarget(name: "MeowMusicPlatformTests", dependencies: ["MeowAccounts", "MeowContracts", "MeowJSON", "MeowMusicFeature", "MeowMusicPlatform", "MeowStorage"]),
        .testTarget(name: "MeowPlaybackTests", dependencies: ["MeowContracts", "MeowMusicFeature", "MeowPlayback"],
                    resources: [.copy("Video/Fixtures")]),
        .testTarget(name: "MeowRuntimeTests", dependencies: ["MeowJSON", "MeowRuntime"]),
        .testTarget(name: "MeowUITests", dependencies: ["MeowUI"]),
        .testTarget(name: "MeowFilmFeatureTests", dependencies: ["MeowFilmFeature", "MeowStorage", "MeowJSON", "MeowRuntime",
            .product(name: "MeowJavaScript", package: "javascript"),
            .product(name: "FilmRuleProgram", package: "film-rules")]),
        .testTarget(name: "MeowJavaScriptTests", dependencies: [
            .product(name: "MeowJavaScript", package: "javascript")])
    ],
    swiftLanguageVersions: [.v5]
)
