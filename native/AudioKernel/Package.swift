// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "AudioKernel",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "CMeowAudioKernel", targets: ["CMeowAudioKernel"])],
    targets: [
        .binaryTarget(name: "CMeowAudioKernel", path: ".cache/Artifacts/CMeowAudioKernel.xcframework")
    ]
)
