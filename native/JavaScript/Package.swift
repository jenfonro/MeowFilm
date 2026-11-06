// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "MeowJavaScript",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "MeowJavaScript", targets: ["MeowJavaScript"])],
    targets: [
        .target(name: "CMeowJavaScript", path: ".",
                exclude: ["Sources", "README.md", "CMakeLists.txt"],
                sources: ["meowfilm_js.c", "quickjs/quickjs.c", "quickjs/dtoa.c",
                          "quickjs/libregexp.c", "quickjs/libunicode.c"],
                publicHeadersPath: "include",
                cSettings: [.headerSearchPath("quickjs"), .define("_GNU_SOURCE")],
                linkerSettings: [.linkedLibrary("m")]),
        .target(name: "MeowJavaScript", dependencies: ["CMeowJavaScript"],
                path: "Sources/MeowJavaScript")
    ],
    swiftLanguageVersions: [.v5],
    cLanguageStandard: .c11
)
