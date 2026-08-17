import XCTest
@testable import TBDoctor

/// Identity that survives a rename without becoming a tracking identifier.
///
/// Every test owns its own directory and calls `resolve(in:)` directly. None
/// of them touch `Identity.current` or the process data directory: that is
/// process-global state, and a fixture that redirected it would reach into
/// every other test running beside it.
final class IdentityTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tbdoctor-identity-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    private func writeIdentity(_ json: String) throws {
        try json.data(using: .utf8)!.write(to: directory.appendingPathComponent("identity.json"))
    }

    func testTheIdentityWeGenerateSatisfiesTheFormatWeDemand() throws {
        let identity = try XCTUnwrap(Identity.resolve(in: directory))

        // The rule the dashboard applies, applied to what we actually write.
        // Without this the producer can emit documents its own consumer
        // refuses to parse, and nothing on either side would notice.
        XCTAssertTrue(Identity.isRandomUUID(identity.hostId))
        XCTAssertEqual(identity.hostId, identity.hostId.lowercased())
        // Reading it back is the same identity, not a new one.
        XCTAssertEqual(identity.hostId, Identity.resolve(in: directory)?.hostId)
    }

    func testAnIdentityThatIsNotARandomUUIDIsRejected() throws {
        let key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        for hostId in [
            "not-a-uuid",
            "",
            // Parses with UUID(uuidString:) and is not one of ours: a v1 UUID
            // encodes a MAC address and a timestamp, exactly the
            // hardware-derived, globally linkable identifier this avoids.
            "3f9a1c2e-7b4d-1a61-9c8f-2e5b7d1a4c60",
            // v4 version nibble, reserved variant — not RFC 4122 random.
            "3f9a1c2e-7b4d-4a61-fc8f-2e5b7d1a4c60",
        ] {
            try writeIdentity(#"{"hostId":"\#(hostId)","installationKey":"\#(key)"}"#)
            XCTAssertFalse(Identity.isRandomUUID(hostId), hostId)
            // A file that is there and unusable is not a licence to mint a new
            // one: an identity that changes between runs splits one endpoint
            // into many, so the honest answer is none.
            XCTAssertNil(Identity.resolve(in: directory), hostId)
        }
    }

    func testACorruptIdentityFileYieldsNoIdentityRatherThanANewOne() throws {
        try writeIdentity(#"{"hostId":"11111111-1111-4111-8111-111111111111","installationKey":"AAAA"}"#)
        XCTAssertNil(Identity.resolve(in: directory))   // key too short to be ours

        try writeIdentity("{ not json")
        XCTAssertNil(Identity.resolve(in: directory))
    }

    func testUnitKeyDistinguishesTwoUnitsOfTheSameModelAndIsAbsentWithoutASerial() throws {
        let identity = try XCTUnwrap(Identity.resolve(in: directory))
        let first = try XCTUnwrap(identity.unitKey(forModel: "045E:0963", serial: "SERIAL-A"))

        XCTAssertEqual(first.count, 16)
        XCTAssertNotNil(first.range(of: "^[0-9a-f]{16}$", options: .regularExpression))
        XCTAssertNotEqual(first, identity.unitKey(forModel: "045E:0963", serial: "SERIAL-B"))
        XCTAssertEqual(first, identity.unitKey(forModel: "045E:0963", serial: "SERIAL-A"))   // stable
        XCTAssertNil(identity.unitKey(forModel: "045E:0963", serial: nil))                   // unit unknown
        XCTAssertNil(identity.unitKey(forModel: "045E:0963", serial: ""))
    }

    func testTwoProductsReportingTheSameSerialAreNotOneUnit() throws {
        let identity = try XCTUnwrap(Identity.resolve(in: directory))

        // Placeholder serials are ordinary in the wild — "0001", "1.00", "0".
        // Hashing the serial alone made any two products reporting the same
        // string collapse to one key, and consumers are told equal keys mean
        // equal physical units, so that is a wrong answer rather than a weak
        // one. The model is part of the hash input for exactly this case.
        for serial in ["0001", "1.00", "0"] {
            XCTAssertNotEqual(
                identity.unitKey(forModel: "045E:0963", serial: serial),
                identity.unitKey(forModel: "046D:C08A", serial: serial),
                serial)
        }

        // Same product, same reported serial: one unit as far as anything
        // outside can tell. This is the limit the scheme does not remove, and
        // it is deliberate rather than overlooked.
        XCTAssertEqual(
            identity.unitKey(forModel: "045E:0963", serial: "0001"),
            identity.unitKey(forModel: "045e:0963", serial: "0001"))

        // No model, no key: the promise cannot be met without one.
        XCTAssertNil(identity.unitKey(forModel: nil, serial: "SERIAL-A"))
        XCTAssertNil(identity.unitKey(forModel: "", serial: "SERIAL-A"))
    }

    func testUnitKeyIsKeyedPerInstallationAndNeverExposesTheSerial() throws {
        let serial = "0123456789AB"
        let identity = try XCTUnwrap(Identity.resolve(in: directory))
        let key = try XCTUnwrap(identity.unitKey(forModel: "045E:0963", serial: serial))

        XCTAssertNil(key.range(of: serial, options: .caseInsensitive))

        // Another installation keys the same serial differently — that is what
        // stops the value correlating one dock across two shared bundles.
        let other = FileManager.default.temporaryDirectory
            .appendingPathComponent("tbdoctor-identity-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: other) }
        let elsewhere = try XCTUnwrap(Identity.resolve(in: other))

        XCTAssertNotEqual(key, elsewhere.unitKey(forModel: "045E:0963", serial: serial))
        XCTAssertNotEqual(identity.hostId, elsewhere.hostId)
    }
}
