import Foundation

/// A Thunderbolt device hanging off one of the host controllers.
/// Depth 0 entries are the Mac's own controllers and are filtered out before
/// we ever build one of these — anything here is a real attached device.
struct TBDevice: Codable, Hashable, Identifiable {
    var vendor: String
    var model: String
    var depth: Int
    var route: Int
    var uid: String
    var linkGbps: Double?

    var id: String { uid }
    var label: String { "\(vendor) \(model)" }
}

struct AdapterInfo: Codable, Hashable {
    var watts: Int?
    var id: Int?
    var name: String?
    var serial: String?
    var manufacturer: String?

    var isPresent: Bool { watts != nil }

    /// Docks and hubs supply power without identifying themselves, so an
    /// adapter with no ID and no manufacturer string is almost certainly the
    /// dock rather than a wall charger. This distinction is the whole reason
    /// the tool exists.
    var looksLikeDock: Bool { isPresent && (id ?? 0) == 0 && manufacturer == nil }

    /// Apple's adapter `name` already embeds the adapter's *rating*
    /// ("70W USB-C Power Adapter") while `watts` is what it actually negotiated,
    /// so the two are shown distinctly rather than concatenated into
    /// nonsense like "68W 70W USB-C Power Adapter".
    var summary: String {
        guard let w = watts else { return "none" }
        if let n = name?.trimmingCharacters(in: .whitespaces), !n.isEmpty {
            return "\(n) (\(w)W)"
        }
        return looksLikeDock ? "\(w)W (dock, unidentified)" : "\(w)W"
    }
}

struct USBDevice: Codable, Hashable, Identifiable {
    var name: String
    var speed: Int
    var locationID: UInt32

    var id: UInt32 { locationID }

    /// IOKit "Device Speed" enum. 4 is SuperSpeed+ on current hardware.
    var speedLabel: String {
        switch speed {
        case 0: return "1.5 Mb/s"
        case 1: return "12 Mb/s"
        case 2: return "480 Mb/s"
        case 3: return "5 Gb/s"
        case 4: return "10 Gb/s"
        default: return "?"
        }
    }
}

struct Sample: Codable {
    var t: Date
    var tb: [TBDevice]
    var adapter: AdapterInfo
    var externalConnected: Bool
    var amperageMilliAmps: Int
    var voltage: Double
    var percent: Int
    var usb: [USBDevice]

    var tbConnected: Bool { !tb.isEmpty }

    /// Negative while discharging. This is the number that exposed the deficit:
    /// a laptop pulling watts out of the battery while nominally on AC power.
    var batteryWatts: Double { Double(amperageMilliAmps) / 1000.0 * voltage }
}

enum EventKind: String, Codable {
    case linkDrop
    case cableChange
    case usb3ModeFail
    case changeBitsFail
    case adapterChange
    case other

    var label: String {
        switch self {
        case .linkDrop: return "Thunderbolt link drop"
        case .cableChange: return "port power cycle"
        case .usb3ModeFail: return "USB3 mode failure"
        case .changeBitsFail: return "hub port error"
        case .adapterChange: return "power adapter change"
        case .other: return "other"
        }
    }

    /// linkDrop is the only event that identifies the *origin* of a fault.
    /// Everything else is usually downstream fallout, which is exactly the
    /// trap this tool exists to keep you out of.
    var isRoot: Bool { self == .linkDrop }
}

struct KernelEvent: Codable, Hashable, Identifiable {
    var t: Date
    var kind: EventKind
    var port: String?
    var message: String

    var id: String { "\(t.timeIntervalSince1970)-\(message.hashValue)" }
}

/// A contiguous run of trouble, stitched together from events plus the sample
/// stream. This is the unit the UI reports, because a single dropped cable
/// produces hundreds of raw events and none of them individually mean anything.
struct Incident: Codable, Identifiable {
    var start: Date
    var end: Date?
    var eventCount: Int
    var rootEventCount: Int
    var peakDischargeMilliAmps: Int?
    var adapterAtStart: AdapterInfo?
    var devicesLost: [String]

    var id: Double { start.timeIntervalSince1970 }
    var duration: TimeInterval? { end.map { $0.timeIntervalSince(start) } }

    var headline: String {
        var parts: [String] = []
        if rootEventCount > 0 { parts.append("link drop") }
        else { parts.append("\(eventCount) port events") }
        // A single-event incident has start == end; printing "0s" implies we
        // measured a zero-length fault rather than having only one timestamp.
        if let d = duration, d >= 1 { parts.append(String(format: "%.0fs", d)) }
        if let a = adapterAtStart, a.looksLikeDock { parts.append("dock-powered") }
        return parts.joined(separator: ", ")
    }
}
