// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TBDoctor",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "TBDoctor",
            path: "Sources/TBDoctor",
            // Connection Dashboard bundle, staged by scripts/build-ui.sh. The
            // directory is committed empty so a plain source build still works;
            // Serve reports an absent bundle rather than failing.
            resources: [.copy("ui")],
            swiftSettings: [.swiftLanguageMode(.v5)],
            linkerSettings: [.linkedFramework("IOKit")]
        )
    ]
)
