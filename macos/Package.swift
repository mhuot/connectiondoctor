// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TBDoctor",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "TBDoctor",
            path: "Sources/TBDoctor",
            swiftSettings: [.swiftLanguageMode(.v5)],
            linkerSettings: [.linkedFramework("IOKit")]
        )
    ]
)
