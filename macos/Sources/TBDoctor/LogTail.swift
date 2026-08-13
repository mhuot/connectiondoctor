import Foundation

/// Kernel event source. `log stream` is the only way to see link-layer events,
/// but it silently drops messages under exactly the burst conditions we care
/// about — a real fault emits hundreds of lines per second. So the stream is
/// paired with a periodic `log show` sweep that backfills whatever was dropped,
/// deduplicated on the way in.
final class LogTail {

    /// Kept deliberately narrow. Every extra term widens the firehose and makes
    /// drops more likely, which defeats the purpose.
    static let predicate = [
        #"eventMessage CONTAINS "unplug on primary lane""#,
        #"eventMessage CONTAINS "cableChangeOccurred""#,
        #"eventMessage CONTAINS "setUSB3Mode""#,
        #"eventMessage CONTAINS "clearing change bits""#,
        #"eventMessage CONTAINS "poweradapter""#
    ].joined(separator: " OR ")

    private var streamProcess: Process?
    private var seen = Set<String>()
    private let queue = DispatchQueue(label: "tbdoctor.logtail")
    private let onEvent: ([KernelEvent]) -> Void

    init(onEvent: @escaping ([KernelEvent]) -> Void) {
        self.onEvent = onEvent
    }

    // MARK: - Lifecycle

    func start() {
        startStream()
        // Backfill on a slower cadence than the stream, with an overlapping
        // window so nothing falls between sweeps.
        Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            self?.queue.async { self?.backfill(seconds: 90) }
        }
    }

    func stop() {
        streamProcess?.terminate()
        streamProcess = nil
    }

    // MARK: - Live stream

    private func startStream() {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/log")
        process.arguments = ["stream", "--style", "ndjson", "--predicate", Self.predicate]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice

        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
            self?.queue.async { self?.ingest(ndjson: text) }
        }

        do {
            try process.run()
            streamProcess = process
        } catch {
            NSLog("TBDoctor: could not start log stream: \(error)")
        }
    }

    // MARK: - Backfill

    private func backfill(seconds: Int) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/log")
        process.arguments = ["show", "--last", "\(seconds)s", "--style", "ndjson", "--predicate", Self.predicate]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice

        do {
            try process.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            if let text = String(data: data, encoding: .utf8) { ingest(ndjson: text) }
        } catch {
            NSLog("TBDoctor: backfill failed: \(error)")
        }
    }

    // MARK: - Parsing

    private func ingest(ndjson: String) {
        var fresh: [KernelEvent] = []

        for line in ndjson.split(separator: "\n") {
            guard let data = line.data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let message = object["eventMessage"] as? String,
                  let timestamp = object["timestamp"] as? String
            else { continue }

            // Our own predicate string appears in the log whenever `log` itself
            // runs, which would otherwise register as a fault. Learned this the
            // hard way: it produced a phantom "overcurrent" finding.
            if message.contains("log run noninteractively") || message.contains("--predicate") { continue }

            guard let date = Self.parse(timestamp: timestamp) else { continue }

            let key = "\(timestamp)|\(message.prefix(120))"
            guard !seen.contains(key) else { continue }
            seen.insert(key)

            fresh.append(KernelEvent(
                t: date,
                kind: Self.classify(message),
                port: Self.port(in: message),
                message: message))
        }

        // Bound the dedup set so a long-running session cannot grow without limit.
        if seen.count > 20_000 { seen.removeAll(keepingCapacity: true) }

        guard !fresh.isEmpty else { return }
        let sorted = fresh.sorted { $0.t < $1.t }
        DispatchQueue.main.async { self.onEvent(sorted) }
    }

    static func classify(_ message: String) -> EventKind {
        if message.contains("unplug on primary lane") { return .linkDrop }
        if message.contains("cableChangeOccurred") { return .cableChange }
        if message.contains("setUSB3Mode") { return .usb3ModeFail }
        if message.contains("clearing change bits") { return .changeBitsFail }
        if message.localizedCaseInsensitiveContains("poweradapter") { return .adapterChange }
        return .other
    }

    /// Pulls the `Something@0113 4300` style port identifier out of a kernel line
    /// so events can be attributed to a place in the topology.
    static func port(in message: String) -> String? {
        guard let range = message.range(of: #"[A-Za-z0-9]+@[0-9a-fA-F]+"#, options: .regularExpression) else { return nil }
        return String(message[range])
    }

    private static let formatters: [DateFormatter] = ["yyyy-MM-dd HH:mm:ss.SSSSSSZ", "yyyy-MM-dd HH:mm:ssZ"].map {
        let f = DateFormatter()
        f.dateFormat = $0
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }

    static func parse(timestamp: String) -> Date? {
        for formatter in formatters {
            if let date = formatter.date(from: timestamp) { return date }
        }
        return nil
    }
}
