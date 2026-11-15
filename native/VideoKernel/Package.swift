// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "VideoKernel",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "MeowVideoKernel", targets: ["MeowVideoKernel"])],
    dependencies: [.package(path: "../KSPlayer")],
    targets: [
        .target(name: "MeowVideoKernel", dependencies: [.product(name: "KSPlayer", package: "ksplayer")]),
        .testTarget(name: "VideoKernelTests", dependencies: ["MeowVideoKernel"],
                    resources: [.copy("Fixtures")])
    ],
    swiftLanguageVersions: [.v5]
)
