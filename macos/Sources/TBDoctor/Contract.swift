import Foundation

/// Connection Contract v1 emission — the shared schema consumed by the
/// dashboard and (eventually) ConnectionDoctor's exports. Spec:
/// docs/schema-v1.md (repo root). Additive-only; adapter serials deliberately omitted
/// from anything that leaves the machine.
enum Contract {

    static let schema = "connection-contract/v1"

    /// - Parameter analysis: findings/incidents/analysis from the recorded
    ///   history (`Analysis.run()`), or nil to omit them — which is what a
    ///   `fullSnapshot` sync point inside the events log does, and what
    ///   "no history" means (absent ≠ empty).
    static func envelope(from sample: Sample, analysis: Analysis.Result? = nil) -> [String: Any] {
        var nodes: [[String: Any]] = []

        // Host is the root of the data tree; power is envelope-level, not a node.
        nodes.append([
            "id": "host",
            "kind": "host",
            "name": Host.current().localizedName ?? ProcessInfo.processInfo.hostName,
            "protocol": "power",
        ])

        // Thunderbolt chain: shallower devices parent deeper ones.
        let chain = sample.tb.sorted { $0.depth < $1.depth }
        var previousTB = "host"
        var firstTBID: String?
        for device in chain {
            let id = "tb:\(device.uid)"
            var node: [String: Any] = [
                "id": id,
                "parentId": previousTB,
                "kind": "thunderbolt",
                "name": device.label,
                "protocol": "thunderbolt",
                "tunneled": false,
            ]
            var tb: [String: Any] = ["routeString": device.route, "depth": device.depth]
            if let gbps = device.linkGbps { tb["linkGbps"] = gbps }
            node["tb"] = tb
            nodes.append(node)
            if firstTBID == nil { firstTBID = id }
            previousTB = id
        }

        // USB tree from location nibbles; roots attach to the TB head or host.
        let byLocation = Dictionary(sample.usb.map { ($0.locationID, $0) },
                                    uniquingKeysWith: { a, _ in a })
        let childCounts = usbChildCounts(sample.usb, byLocation: byLocation)
        for device in sample.usb {
            nodes.append(usbNode(device,
                                 byLocation: byLocation,
                                 childCount: childCounts[device.locationID] ?? 0,
                                 fallbackParent: firstTBID ?? "host",
                                 tunnelCapable: firstTBID != nil))
        }

        var envelope: [String: Any] = [
            "schema": schema,
            "capturedAt": ISO8601DateFormatter().string(from: sample.t),
            "host": hostInfo(),
            "power": power(sample),
            "nodes": nodes,
        ]
        envelope["displaysKnown"] = sample.displaysKnown
        if sample.displaysKnown {
            envelope["displays"] = sample.displays.map { display -> [String: Any] in
                var out: [String: Any] = [
                    "name": display.name,
                    "widthPx": display.width,
                    "heightPx": display.height,
                    "builtIn": display.isBuiltIn,
                ]
                if let hz = display.refreshHz { out["refreshHz"] = hz }
                return out
            }
        }
        if let analysis {
            envelope["findings"] = Analysis.findingsJSON(analysis.findings)
            envelope["incidents"] = Analysis.incidentsJSON(analysis.incidents)
            envelope["analysis"] = Analysis.analysisJSON(analysis)
        }
        return envelope
    }

    static func json(from sample: Sample, analysis: Analysis.Result? = nil) throws -> Data {
        try JSONSerialization.data(withJSONObject: envelope(from: sample, analysis: analysis),
                                   options: [.prettyPrinted, .sortedKeys])
    }

    // MARK: - Pieces

    private static let builtInMarkers = ["Internal", "FaceTime", "Ambient Light", "Touch Bar", "iBridge", "Built-in", "Headset"]
    static func isBuiltIn(_ device: USBDevice) -> Bool {
        guard device.vendorID == 0x05AC else { return false }
        return builtInMarkers.contains { device.name.localizedCaseInsensitiveContains($0) }
    }

    private static func hostInfo() -> [String: Any] {
        var info: [String: Any] = [
            "name": Host.current().localizedName ?? ProcessInfo.processInfo.hostName,
            "os": "macos",
            "arch": machineArch(),
        ]
        if let model = sysctlString("hw.model") { info["model"] = model }
        // Random and per-installation, and omitted entirely when there is no
        // durable identity: an id that changes between runs would split one
        // endpoint into many, so absent (fall back to the hostname) is the
        // honest answer.
        if let identity = Identity.current { info["id"] = identity.hostId }
        return info
    }

    private static func power(_ sample: Sample) -> [String: Any] {
        let hasBattery = sample.hasBattery ?? true
        let source: String
        if !hasBattery {
            source = "mains"
        } else if sample.adapter.looksLikeDock {
            source = "dock"
        } else if sample.adapter.isPresent {
            source = "adapter"
        } else {
            source = "battery"
        }
        var out: [String: Any] = [
            "source": source,
            "externalConnected": sample.externalConnected,
            "batteryPresent": hasBattery,
        ]
        if hasBattery {
            out["batteryPercent"] = sample.percent
            // mA × V = mW; negative while discharging — the deficit signal.
            out["batteryRateMilliwatts"] = Int(Double(sample.amperageMilliAmps) * sample.voltage)
        }
        if sample.adapter.isPresent {
            var adapter: [String: Any] = ["identifiesItself": !sample.adapter.looksLikeDock]
            if let watts = sample.adapter.watts { adapter["watts"] = watts }
            if let name = sample.adapter.name?.trimmingCharacters(in: .whitespaces), !name.isEmpty {
                adapter["name"] = name
            }
            if let vendor = sample.adapter.manufacturer { adapter["vendor"] = vendor }
            // Serial intentionally omitted: exports leave the machine.
            out["adapter"] = adapter
        }
        return out
    }

    private static func usbChildCounts(
        _ devices: [USBDevice],
        byLocation: [UInt32: USBDevice]
    ) -> [UInt32: Int] {
        var counts: [UInt32: Int] = [:]
        for device in devices {
            var parent = device.parentLocationID
            while let candidate = parent {
                if byLocation[candidate] != nil {
                    counts[candidate, default: 0] += 1
                    break
                }
                parent = USBDevice(name: "", speed: 0, locationID: candidate).parentLocationID
            }
        }
        return counts
    }

    private static func usbNode(
        _ device: USBDevice,
        byLocation: [UInt32: USBDevice],
        childCount: Int,
        fallbackParent: String,
        tunnelCapable: Bool
    ) -> [String: Any] {
        var parentID = fallbackParent
        var parent = device.parentLocationID
        while let candidate = parent {
            if let owner = byLocation[candidate] {
                parentID = String(format: "usb:0x%08X", owner.locationID)
                break
            }
            parent = USBDevice(name: "", speed: 0, locationID: candidate).parentLocationID
        }

        let (proto, bits): (String, Int?) = {
            switch device.speed {
            case 0: return ("usbLow", 1_500_000)
            case 1: return ("usbLow", 12_000_000)
            case 2: return ("usb2", 480_000_000)
            case 3: return ("usb3", 5_000_000_000)
            case 4: return ("usb3", 10_000_000_000)
            default: return ("unknown", nil)
            }
        }()

        var node: [String: Any] = [
            "id": String(format: "usb:0x%08X", device.locationID),
            "parentId": parentID,
            "kind": (device.deviceClass == 9 || childCount > 0) ? "hub" : "device",
            "name": device.name,
            "protocol": proto,
            // Only USB3 rides a tunnel, and only when a Thunderbolt link exists.
            "tunneled": proto == "usb3" && tunnelCapable,
        ]
        if let vendor = device.vendorName { node["vendorName"] = vendor }
        if let vidPid = device.vidPid { node["vidPid"] = vidPid }
        // Two identical docks are two units here, and nothing anywhere else:
        // the serial is keyed with a secret that never leaves this machine.
        if let unitKey = Identity.unitKey(forSerial: device.serial) { node["unitKey"] = unitKey }
        if let bits { node["linkBitsPerSecond"] = bits }
        if let usbClass = device.deviceClass { node["usbClass"] = usbClass }
        // Producer classification of integrated devices (dashboard-topology-
        // controls): Apple's internal keyboard/trackpad, camera, ambient light
        // sensor, Touch Bar and bridge silicon enumerate as USB devices on
        // every Mac; nobody plugged them in. Absent means unknown, never
        // external, so only assert true.
        if isBuiltIn(device) { node["builtIn"] = true }
        node["platform"] = ["locationID": Int(device.locationID)]
        return node
    }

    private static func machineArch() -> String {
        var systemInfo = utsname()
        uname(&systemInfo)
        return withUnsafePointer(to: &systemInfo.machine) {
            $0.withMemoryRebound(to: CChar.self, capacity: 1) { String(cString: $0) }
        }
    }

    private static func sysctlString(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &buffer, &size, nil, 0) == 0 else { return nil }
        return String(cString: buffer)
    }
}
