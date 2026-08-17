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
        ),
        // Identity is the first thing here worth testing away from a real
        // machine: it is pure logic over a directory, it decides what every
        // document says about who produced it, and its rules have to match the
        // dashboard's parser exactly. Wider producer coverage is
        // contract-conformance 1.6b.
        .testTarget(
            name: "TBDoctorTests",
            dependencies: ["TBDoctor"],
            path: "Tests/TBDoctorTests",
            swiftSettings: [.swiftLanguageMode(.v5)]
        )
    ]
)
