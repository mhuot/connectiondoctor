import CryptoKit
import Foundation

/// Who this machine is, and how to tell two identical devices apart — without
/// becoming a tracking identifier, and without ever inventing an identity it
/// cannot keep.
///
/// Neither value is derived from hardware. A hash of IOPlatformUUID would be
/// stable across every export forever, which is pseudonymisation rather than
/// privacy: anyone holding two unrelated bundles could link them.
///
/// The harder rule is **durability**: an identity that changes between runs is
/// worse than none, because it silently splits one endpoint into many in every
/// consumer that keys on it. So if the identity cannot be read or created and
/// persisted, this returns nil and the producer emits no identity at all —
/// consumers fall back to the hostname, which is honest, rather than to a
/// process-local random pretending to be an installation.
struct Identity: Codable {
    var hostId: String
    var installationKey: Data

    /// A key shorter than this is not one we wrote; treat the file as corrupt.
    private static let keyBytes = 32
    private static let filename = "identity.json"
    private static let lock = NSLock()
    private static var cached: Identity?
    private static var failureLogged = false

    /// The durable identity, or nil when there is none we can stand behind.
    static var current: Identity? {
        lock.lock()
        defer { lock.unlock() }
        if let cached { return cached }

        let url = Store.directory.appendingPathComponent(filename)
        if let existing = read(url) {
            cached = existing
            return existing
        }

        // Create exactly once across every process that might start together —
        // collector, CLI, HTTP and MCP can all be first. O_EXCL makes the
        // creation atomic; whoever loses re-reads the winner's file rather
        // than caching a value it did not persist.
        var key = Data(count: keyBytes)
        let generated = key.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, keyBytes, $0.baseAddress!) }
        guard generated == errSecSuccess,
              let payload = try? JSONEncoder().encode(Identity(hostId: UUID().uuidString.lowercased(), installationKey: key))
        else {
            report("could not generate an identity")
            return nil
        }

        let descriptor = open(url.path, O_CREAT | O_EXCL | O_WRONLY, 0o600)
        if descriptor >= 0 {
            let written = payload.withUnsafeBytes { write(descriptor, $0.baseAddress, $0.count) }
            close(descriptor)
            if written == payload.count, let readBack = read(url) {
                cached = readBack
                return readBack
            }
            // Wrote nothing usable: leave no half-file behind claiming to be identity.
            try? FileManager.default.removeItem(at: url)
            report("could not persist an identity to \(url.path)")
            return nil
        }

        // Someone else created it (or we cannot write here at all).
        if let winner = read(url) {
            cached = winner
            return winner
        }

        report("no durable identity at \(url.path) — host.id and unitKey will be omitted")
        return nil
    }

    /// A device's identity within this installation: HMAC of its serial under
    /// the installation key, truncated. Nil when there is no durable identity
    /// or the device reports no serial — "same model, unit unknown" is a real
    /// answer, and so is "this machine has no identity to key it with".
    /// A device's identity within this installation: HMAC of its serial under
    /// the installation key, truncated. An instance method, so a caller has to
    /// have resolved the identity first — one document, one answer.
    func unitKey(forSerial serial: String?) -> String? {
        guard let serial, !serial.isEmpty else { return nil }
        let identity = self
        let mac = HMAC<SHA256>.authenticationCode(
            for: Data(serial.utf8),
            using: SymmetricKey(data: identity.installationKey))
        return mac.map { String(format: "%02x", $0) }.joined().prefix(16).description
    }

    private static func read(_ url: URL) -> Identity? {
        guard let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode(Identity.self, from: data),
              UUID(uuidString: decoded.hostId) != nil,
              decoded.installationKey.count == keyBytes
        else { return nil }
        return decoded
    }

    /// Say it once: a machine with no identity should not fill the log with it.
    private static func report(_ message: String) {
        guard !failureLogged else { return }
        failureLogged = true
        FileHandle.standardError.write("TBDoctor: \(message)\n".data(using: .utf8)!)
    }

    /// Test seam: forget the cached identity so a fixture directory gets its own.
    static func resetCacheForTesting() {
        lock.lock(); cached = nil; failureLogged = false; lock.unlock()
    }
}
