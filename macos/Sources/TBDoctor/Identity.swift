import CryptoKit
import Foundation

/// Who this machine is, and how to tell two identical devices apart — without
/// becoming a tracking identifier.
///
/// Both values are generated here and never derived from hardware. A hash of
/// IOPlatformUUID would be stable across every export forever, which is
/// pseudonymisation, not privacy: anyone holding two unrelated bundles could
/// link them. Instead:
///
/// - `hostId` is a random UUID for *this installation*. It survives hostname
///   changes (the thing it exists to fix) and upgrades, and regenerates only
///   when the data directory is reset.
/// - `installationKey` is a random secret that never leaves the machine. Device
///   serials are keyed with it, so `unitKey` distinguishes two identical docks
///   *here* while being meaningless anywhere else.
struct Identity: Codable {
    var hostId: String
    var installationKey: Data

    private static let filename = "identity.json"
    private static let lock = NSLock()
    private static var cached: Identity?

    static var current: Identity {
        lock.lock()
        defer { lock.unlock() }
        if let cached { return cached }

        let url = Store.directory.appendingPathComponent(filename)
        if let data = try? Data(contentsOf: url),
           let existing = try? JSONDecoder().decode(Identity.self, from: data) {
            cached = existing
            return existing
        }

        var key = Data(count: 32)
        _ = key.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!) }
        let fresh = Identity(hostId: UUID().uuidString.lowercased(), installationKey: key)
        if let data = try? JSONEncoder().encode(fresh) {
            // Owner-only: the key is what makes unitKey local rather than global.
            try? data.write(to: url, options: [.atomic, .completeFileProtection])
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }
        cached = fresh
        return fresh
    }

    /// A device's identity within this installation: HMAC of its serial under
    /// the installation key, truncated. Nil when the device reports no serial —
    /// "same model, unit unknown" is a real answer and better than a guess.
    static func unitKey(forSerial serial: String?) -> String? {
        guard let serial, !serial.isEmpty else { return nil }
        let mac = HMAC<SHA256>.authenticationCode(
            for: Data(serial.utf8),
            using: SymmetricKey(data: current.installationKey))
        return mac.map { String(format: "%02x", $0) }.joined().prefix(16).description
    }

    /// Test seam: forget the cached identity so a fixture directory gets its own.
    static func resetCacheForTesting() {
        lock.lock(); cached = nil; lock.unlock()
    }
}
