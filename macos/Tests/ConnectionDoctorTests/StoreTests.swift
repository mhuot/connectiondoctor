import XCTest
@testable import ConnectionDoctor

final class StoreTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("connectiondoctor-store-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    func testMigratesLegacyDefaultWhenNewDirectoryIsAbsent() throws {
        let legacy = root.appendingPathComponent("TBDoctor")
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        try Data("history".utf8).write(to: legacy.appendingPathComponent("samples.jsonl"))
        var migrationHandled = false

        let resolved = Store.resolveDirectory(
            environment: [:],
            applicationSupport: root,
            onMigration: { migrationHandled = true })

        XCTAssertEqual(resolved.lastPathComponent, "ConnectionDoctor")
        XCTAssertFalse(FileManager.default.fileExists(atPath: legacy.path))
        XCTAssertEqual(
            try String(contentsOf: resolved.appendingPathComponent("samples.jsonl"), encoding: .utf8),
            "history")
        XCTAssertTrue(migrationHandled)
    }

    func testLeavesLegacyDirectoryUntouchedWhenNewDirectoryExists() throws {
        let legacy = root.appendingPathComponent("TBDoctor")
        let current = root.appendingPathComponent("ConnectionDoctor")
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: current, withIntermediateDirectories: true)
        try Data("legacy".utf8).write(to: legacy.appendingPathComponent("samples.jsonl"))
        try Data("current".utf8).write(to: current.appendingPathComponent("samples.jsonl"))
        var migrationHandled = false

        let resolved = Store.resolveDirectory(
            environment: [:],
            applicationSupport: root,
            onMigration: { migrationHandled = true })

        XCTAssertEqual(resolved.standardizedFileURL.path, current.standardizedFileURL.path)
        XCTAssertEqual(
            try String(contentsOf: legacy.appendingPathComponent("samples.jsonl"), encoding: .utf8),
            "legacy")
        XCTAssertEqual(
            try String(contentsOf: current.appendingPathComponent("samples.jsonl"), encoding: .utf8),
            "current")
        XCTAssertFalse(migrationHandled)
    }

    func testCanonicalOverrideTakesPrecedenceOverLegacyOverride() {
        let canonical = root.appendingPathComponent("canonical")
        let legacy = root.appendingPathComponent("legacy")

        let resolved = Store.resolveDirectory(environment: [
            "CONNECTIONDOCTOR_DIR": canonical.path,
            "TBDOCTOR_DIR": legacy.path,
        ])

        XCTAssertEqual(resolved.standardizedFileURL.path, canonical.standardizedFileURL.path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: canonical.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: legacy.path))
    }

    func testLegacyOverrideRemainsSupported() {
        let legacy = root.appendingPathComponent("legacy")

        let resolved = Store.resolveDirectory(environment: ["TBDOCTOR_DIR": legacy.path])

        XCTAssertEqual(resolved.standardizedFileURL.path, legacy.standardizedFileURL.path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path))
    }
}
