import Foundation

/// Contract phase 2: a v1 events JSONL stream written alongside the legacy
/// stores. Change events are derived by diffing consecutive samples; kernel
/// link drops map to `linkDown` (the root kind); hourly `fullSnapshot` events
/// embed a complete envelope as a sync point.
final class ContractLog {

    static let filename = "contract-events.jsonl"
    static var path: URL { Store.directory.appendingPathComponent(filename) }

    private let queue = DispatchQueue(label: "tbdoctor.contractlog")
    private let maxBytes = 24 * 1024 * 1024
    private var previous: Sample?
    private var lastFullSnapshot: Date?
    private var wasDeficit = false
    private let fullSnapshotInterval: TimeInterval = 3600

    /// Absorb one sample; emit whatever changed since the last one.
    func record(_ sample: Sample, kernelEvents: [KernelEvent] = []) {
        var lines: [[String: Any]] = []
        let stamp = ISO8601DateFormatter().string(from: sample.t)

        if let prev = previous {
            // Link state: contract linkDown/linkUp from TB presence transitions;
            // kernel drops carry the precise root timestamp when we have one.
            if prev.tbConnected && !sample.tbConnected {
                let kernelDrop = kernelEvents.first { $0.kind == .linkDrop }
                lines.append([
                    "t": kernelDrop.map { ISO8601DateFormatter().string(from: $0.t) } ?? stamp,
                    "kind": "linkDown",
                ])
            } else if !prev.tbConnected && sample.tbConnected {
                lines.append(["t": stamp, "kind": "linkUp"])
            }

            // Device changes by locationID, named with VID:PID identity.
            let before = Dictionary(prev.usb.map { ($0.locationID, $0) }, uniquingKeysWith: { a, _ in a })
            let after = Dictionary(sample.usb.map { ($0.locationID, $0) }, uniquingKeysWith: { a, _ in a })
            for (loc, device) in before where after[loc] == nil {
                lines.append(deviceEvent("deviceRemoved", device, at: stamp))
            }
            for (loc, device) in after where before[loc] == nil {
                lines.append(deviceEvent("deviceAdded", device, at: stamp))
            }

            if prev.adapter.id != sample.adapter.id || prev.adapter.watts != sample.adapter.watts {
                lines.append(["t": stamp, "kind": "adapterChanged"])
            }
        }

        // Deficit transitions use the contract's shared 2000mW rule.
        let rateMilliwatts = Double(sample.amperageMilliAmps) * sample.voltage
        let isDeficit = sample.externalConnected && rateMilliwatts <= -2000
        if isDeficit != wasDeficit {
            lines.append(["t": stamp, "kind": isDeficit ? "deficitStart" : "deficitEnd"])
            wasDeficit = isDeficit
        }

        if lastFullSnapshot.map({ sample.t.timeIntervalSince($0) >= fullSnapshotInterval }) ?? true {
            lines.append(["t": stamp, "kind": "fullSnapshot", "snapshot": Contract.envelope(from: sample)])
            lastFullSnapshot = sample.t
        }

        previous = sample
        guard !lines.isEmpty else { return }
        append(lines)
    }

    private func deviceEvent(_ kind: String, _ device: USBDevice, at stamp: String) -> [String: Any] {
        var event: [String: Any] = [
            "t": stamp,
            "kind": kind,
            "nodeId": String(format: "usb:0x%08X", device.locationID),
            "name": device.name,
        ]
        if let vidPid = device.vidPid { event["vidPid"] = vidPid }
        return event
    }

    private func append(_ lines: [[String: Any]]) {
        queue.async {
            let url = ContractLog.path
            if !FileManager.default.fileExists(atPath: url.path) {
                FileManager.default.createFile(atPath: url.path, contents: nil)
            }
            guard let handle = try? FileHandle(forWritingTo: url) else { return }
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            for line in lines {
                guard var data = try? JSONSerialization.data(withJSONObject: line) else { continue }
                data.append(0x0A)
                try? handle.write(contentsOf: data)
            }
            self.trimIfNeeded(url)
        }
    }

    /// Drop-oldest-half at the cap, cutting on a line boundary — and because a
    /// blind cut could orphan the stream from its last fullSnapshot, force a
    /// fresh snapshot after any trim.
    private func trimIfNeeded(_ url: URL) {
        guard let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int,
              size > maxBytes,
              let text = try? String(contentsOf: url, encoding: .utf8) else { return }
        let lines = text.split(separator: "\n")
        let kept = lines.suffix(lines.count / 2).joined(separator: "\n") + "\n"
        try? kept.write(to: url, atomically: true, encoding: .utf8)
        lastFullSnapshot = nil
    }
}
