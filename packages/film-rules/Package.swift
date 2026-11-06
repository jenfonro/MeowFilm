// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "FilmRuleProgram",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "FilmRuleProgram", targets: ["FilmRuleProgram"])],
    targets: [
        .target(name: "FilmRuleProgram", path: ".",
                exclude: ["README.md", "test"],
                sources: ["Sources/FilmRuleProgram.swift"],
                resources: [.copy("film-rules.js")])
    ],
    swiftLanguageVersions: [.v5]
)
