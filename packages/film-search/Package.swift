// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "FilmSearchProgram",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "FilmSearchProgram", targets: ["FilmSearchProgram"])],
    targets: [
        .target(name: "FilmSearchProgram", path: ".",
                exclude: ["README.md", "test"],
                sources: ["Sources/FilmSearchProgram.swift"],
                resources: [.copy("film-search.js")])
    ],
    swiftLanguageVersions: [.v5]
)
