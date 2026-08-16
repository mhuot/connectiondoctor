import Foundation
import Combine

/// Append-only JSONL persistence. The whole point of the tool is catching a
/// fault you were not watching, so state has to survive on disk rather than
/// living in a window someone has to have open.
final class Store {
    /// Overridable via TBDOCTOR_DIR so fixtures and tests never touch real
    /// recorded history.
    static let directory: URL = {
        let base: URL
        if let override = ProcessInfo.processInfo.environment["TBDOCTOR_DIR"] {
            base = URL(fileURLWithPath: override, isDirectory: true)
        } else {
            base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("TBDoctor", isDirectory: true)
        }
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base
    }()

    private let url: URL
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()
    private let queue = DispatchQueue(label: "tbdoctor.store")

    /// Trim when the file passes this size so an always-on agent cannot fill a disk.
    private let maxBytes = 24 * 1024 * 1024

    init(filename: String) {
        url = Store.directory.appendingPathComponent(filename)
        encoder.dateEncodingStrategy = .iso8601
        decoder.dateDecodingStrategy = .iso8601
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }
    }

    func append<T: Encodable>(_ items: [T]) {
        guard !items.isEmpty else { return }
        queue.async {
            guard let handle = try? FileHandle(forWritingTo: self.url) else { return }
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            for item in items {
                guard var data = try? self.encoder.encode(item) else { continue }
                data.append(0x0A)
                try? handle.write(contentsOf: data)
            }
            self.trimIfNeeded()
        }
    }

    func append<T: Encodable>(_ item: T) { append([item]) }

    func load<T: Decodable>(_ type: T.Type, since: Date? = nil, dateKey: (T) -> Date) -> [T] {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        var out: [T] = []
        for line in text.split(separator: "\n") {
            guard let data = line.data(using: .utf8),
                  let item = try? decoder.decode(T.self, from: data) else { continue }
            if let since, dateKey(item) < since { continue }
            out.append(item)
        }
        return out
    }

    /// Drop the oldest half rather than rotating to a second file — keeps the
    /// read path a single sequential scan.
    private func trimIfNeeded() {
        guard let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int,
              size > maxBytes,
              let text = try? String(contentsOf: url, encoding: .utf8) else { return }
        let lines = text.split(separator: "\n")
        let kept = lines.suffix(lines.count / 2).joined(separator: "\n") + "\n"
        try? kept.write(to: url, atomically: true, encoding: .utf8)
    }
}

/// Owns the sampling loop, the log tail, persistence, and the derived state the
/// UI binds to.
@MainActor
final class Collector: ObservableObject {

    static let shared = Collector()

    @Published private(set) var current: Sample?
    @Published private(set) var samples: [Sample] = []
    @Published private(set) var events: [KernelEvent] = []
    @Published private(set) var incidents: [Incident] = []
    @Published private(set) var findings: [Finding] = []

    /// Baseline cadence. Fine enough to catch a multi-second dropout without
    /// producing an unreasonable volume of samples over a day.
    private let idleInterval: TimeInterval = 5
    /// After any kernel event we tighten up, because that is when resolution
    /// actually matters and the interesting behaviour lasts only seconds.
    private let activeInterval: TimeInterval = 1
    private let activeWindow: TimeInterval = 60

    /// How long the in-memory window is. Disk keeps everything; this bounds the
    /// working set the analyser and charts operate on.
    private let retention: TimeInterval = 6 * 3600

    private var timer: Timer?
    private var currentInterval: TimeInterval = 0
    private var logTail: LogTail?
    private var lastEventAt: Date?
    private var running = false

    private let sampleStore = Store(filename: "samples.jsonl")
    private let eventStore = Store(filename: "events.jsonl")
    private let contractLog = ContractLog()
    /// Kernel events since the last tick, so linkDown carries the root timestamp.
    private var pendingKernelEvents: [KernelEvent] = []

    /// Set when another collector already owns the store. Two collectors
    /// appending to one JSONL interleave their samples and produce duplicate
    /// timestamps, which quietly corrupts every downstream analysis — so the
    /// second one refuses to collect rather than competing.
    @Published private(set) var storeConflict = false

    private var lockDescriptor: Int32 = -1

    var storeDirectory: URL { Store.directory }

    /// Exclusive advisory lock over the store directory. Held for the process
    /// lifetime; the kernel releases it automatically if we are killed.
    private func acquireStoreLock() -> Bool {
        let path = Store.directory.appendingPathComponent(".collector.lock").path
        let descriptor = open(path, O_CREAT | O_RDWR, 0o644)
        guard descriptor >= 0 else { return true } // can't lock: don't block collection
        if flock(descriptor, LOCK_EX | LOCK_NB) != 0 {
            close(descriptor)
            return false
        }
        lockDescriptor = descriptor
        return true
    }

    // MARK: - Lifecycle

    func start() {
        guard !running else { return }
        running = true

        let cutoff = Date().addingTimeInterval(-retention)
        samples = sampleStore.load(Sample.self, since: cutoff) { $0.t }
        events = eventStore.load(KernelEvent.self, since: cutoff) { $0.t }

        // Read history either way so the UI still shows past incidents, but
        // stop before writing anything if another collector owns the store.
        guard acquireStoreLock() else {
            storeConflict = true
            NSLog("TBDoctor: another collector owns \(Store.directory.path); running read-only")
            recompute()
            return
        }

        logTail = LogTail { [weak self] fresh in
            Task { @MainActor in self?.absorb(fresh) }
        }
        logTail?.start()

        tick()
        scheduleTimer(interval: idleInterval)
    }

    func stop() {
        timer?.invalidate()
        logTail?.stop()
    }

    /// Force an immediate sample. The view refreshes on its own each tick, but
    /// after physically re-plugging something you want the answer now, not in
    /// up to five seconds.
    func refreshNow() {
        guard running, !storeConflict else { return }
        tick()
    }

    private func scheduleTimer(interval: TimeInterval) {
        guard interval != currentInterval else { return }
        currentInterval = interval
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    // MARK: - Sampling

    private func tick() {
        let sample = Probes.sample()
        current = sample
        samples.append(sample)
        sampleStore.append(sample)
        contractLog.record(sample, kernelEvents: pendingKernelEvents)
        pendingKernelEvents = []

        // Switch cadence based on whether we are inside an active window.
        let shouldBeFast = lastEventAt.map { Date().timeIntervalSince($0) < activeWindow } ?? false
        scheduleTimer(interval: shouldBeFast ? activeInterval : idleInterval)

        prune()
        recompute()
    }

    private func absorb(_ fresh: [KernelEvent]) {
        guard !fresh.isEmpty else { return }
        events.append(contentsOf: fresh)
        events.sort { $0.t < $1.t }
        eventStore.append(fresh)
        pendingKernelEvents.append(contentsOf: fresh)
        lastEventAt = fresh.last?.t

        // Take an immediate sample so the analyser has power state from the
        // moment of the event rather than up to five seconds later.
        tick()
    }

    private func prune() {
        let cutoff = Date().addingTimeInterval(-retention)
        if let index = samples.firstIndex(where: { $0.t >= cutoff }), index > 0 { samples.removeFirst(index) }
        if let index = events.firstIndex(where: { $0.t >= cutoff }), index > 0 { events.removeFirst(index) }
    }

    private func recompute() {
        incidents = Collector.deriveIncidents(samples: samples, events: events)
        findings = Diagnosis.analyze(samples: samples, events: events)
    }

    // MARK: - Incident stitching

    /// Events arrive in the hundreds during a single fault; individually they
    /// are meaningless. Anything within `gap` of the previous event belongs to
    /// the same incident.
    /// Pure function over the two streams — deliberately `nonisolated` so the
    /// CLI and MCP front ends can call it without a main-actor hop.
    nonisolated static func deriveIncidents(samples: [Sample], events: [KernelEvent], gap: TimeInterval = 30) -> [Incident] {
        guard !events.isEmpty else { return [] }

        var groups: [[KernelEvent]] = []
        var run: [KernelEvent] = [events[0]]
        for event in events.dropFirst() {
            if event.t.timeIntervalSince(run.last!.t) <= gap { run.append(event) }
            else { groups.append(run); run = [event] }
        }
        groups.append(run)

        return groups.compactMap { group -> Incident? in
            guard let first = group.first, let last = group.last else { return nil }
            // A lone adapter change is a plug event, not a fault.
            if group.count == 1 && group[0].kind == .adapterChange { return nil }

            let window = samples.filter { $0.t >= first.t.addingTimeInterval(-5) && $0.t <= last.t.addingTimeInterval(5) }
            let peak = window.map(\.amperageMilliAmps).min()

            var lost: [String] = []
            if let before = samples.last(where: { $0.t < first.t }), let during = window.min(by: { $0.usb.count < $1.usb.count }) {
                let after = Set(during.usb.map { $0.locationID })
                lost = before.usb.filter { !after.contains($0.locationID) }.map(\.name)
            }

            return Incident(
                start: first.t,
                end: last.t,
                eventCount: group.count,
                rootEventCount: group.filter { $0.kind.isRoot }.count,
                peakDischargeMilliAmps: peak,
                adapterAtStart: samples.last(where: { $0.t <= first.t })?.adapter,
                devicesLost: lost)
        }.reversed()
    }

    // MARK: - Derived UI state

    var health: Severity {
        // A dock that is currently absent but was present minutes ago is a
        // fault. A dock that was never there is just an undocked laptop.
        if let sample = current, !sample.tbConnected, hadRecentLink { return .critical }
        return findings.first?.severity ?? .info
    }

    /// True if Thunderbolt was connected at some point recently — distinguishes
    /// "the dock has failed" from "there is simply no dock plugged in", which
    /// should never show as a fault.
    private var hadRecentLink: Bool {
        let cutoff = Date().addingTimeInterval(-300)
        return samples.contains { $0.t >= cutoff && $0.tbConnected }
    }

    var lastIncident: Incident? { incidents.first }
}
