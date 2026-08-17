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
    /// Lets a dock's own USB hubs be recognised as belonging to it.
    var vendorID: Int?

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
    /// Captured because hubs routinely self-describe as "Generic". The vendor ID
    /// is what reveals that a nondescript "4-Port USB 2.0 Hub" is actually the
    /// hub built into a monitor — the single most confusing thing about reading
    /// one of these trees.
    var vendorID: Int?
    var vendorName: String?
    /// Everything below exists to identify hardware that names itself
    /// uselessly. A hub calling itself "USB2.0 Hub" is anonymous; its
    /// VID:PID is not.
    var productID: Int?
    var serial: String?
    var deviceClass: Int?
    var deviceSubClass: Int?
    var deviceProtocol: Int?
    var releaseBCD: Int?
    var usbVersionBCD: Int?
    var linkSpeedBitsPerSecond: Int?
    var usbAddress: Int?

    var id: UInt32 { locationID }

    /// The pair you actually search for. Uppercase hex, no prefix — the form
    /// every USB ID database expects.
    var vidPid: String? {
        guard let vendorID, let productID else { return nil }
        return String(format: "%04X:%04X", vendorID, productID)
    }

    /// BCD-encoded: 0x0201 means USB 2.01.
    static func bcdString(_ value: Int?) -> String? {
        guard let value else { return nil }
        return String(format: "%d.%02d", (value >> 8) & 0xFF, value & 0xFF)
    }

    /// USB device class codes. Worth decoding: class 9 tells you a thing is a
    /// hub even when its name does not.
    static func className(_ code: Int?) -> String? {
        guard let code else { return nil }
        let names: [Int: String] = [
            0x00: "per-interface", 0x01: "audio", 0x02: "communications",
            0x03: "human interface", 0x05: "physical", 0x06: "image",
            0x07: "printer", 0x08: "mass storage", 0x09: "hub",
            0x0A: "CDC data", 0x0B: "smart card", 0x0D: "content security",
            0x0E: "video", 0x0F: "personal healthcare", 0x10: "audio/video",
            0x11: "billboard", 0x12: "USB-C bridge", 0xDC: "diagnostic",
            0xE0: "wireless controller", 0xEF: "miscellaneous",
            0xFE: "application specific", 0xFF: "vendor specific"
        ]
        let name = names[code] ?? "unknown"
        return String(format: "0x%02X (%@)", code, name)
    }

    /// USB location IDs are hierarchical nibbles: 0x02144300 sits under
    /// 0x02144000, which sits under 0x02140000. Clearing the lowest non-zero
    /// nibble walks one level up the physical topology.
    var parentLocationID: UInt32? {
        for shift in stride(from: 0, through: 28, by: 4) {
            if (locationID >> UInt32(shift)) & 0xF != 0 {
                return locationID & ~(UInt32(0xF) << UInt32(shift))
            }
        }
        return nil
    }

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

/// An attached display. DisplayPort is tunneled over Thunderbolt as its own
/// protocol, entirely separate from USB — which is why a monitor is invisible in
/// a USB tree unless it happens to carry a hub.
struct DisplayInfo: Codable, Hashable, Identifiable {
    var name: String
    var width: Int
    var height: Int
    var refreshHz: Double?
    var isBuiltIn: Bool
    var vendorNumber: Int?
    var modelNumber: Int?
    var serialNumber: Int?

    var id: String { "\(name)-\(width)x\(height)-\(vendorNumber ?? 0)-\(modelNumber ?? 0)" }
    var resolution: String { "\(width) × \(height)" }
}

struct Sample: Codable {
    var t: Date
    var tb: [TBDevice]
    var adapter: AdapterInfo
    var externalConnected: Bool
    var amperageMilliAmps: Int
    var voltage: Double
    var percent: Int
    /// nil on recordings made before this field existed; treat as "has one".
    /// False on desktops (Mac mini, Studio, Pro), which expose no battery —
    /// zeros there are absence of hardware, not an empty battery.
    var hasBattery: Bool? = true
    var usb: [USBDevice]
    /// Empty when the process has no window-server session (e.g. over SSH),
    /// which is not the same as "no displays attached".
    var displays: [DisplayInfo] = []
    var displaysKnown: Bool = true

    var tbConnected: Bool { !tb.isEmpty }
    var isDesktop: Bool { hasBattery == false }

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
/// A device that disappeared during an incident, kept with its cross-platform
/// identity so the contract's `devicesLost[{vidPid, name}]` can be emitted.
struct LostDevice: Codable, Hashable {
    var name: String
    var vidPid: String?
    var locationID: UInt32
}

struct Incident: Codable, Identifiable {
    var start: Date
    var end: Date?
    var eventCount: Int
    var rootEventCount: Int
    var peakDischargeMilliAmps: Int?
    /// mA × V at the peak-discharge sample — the contract's unit (mW).
    var peakDischargeMilliwatts: Int?
    var adapterAtStart: AdapterInfo?
    var devicesLost: [String]
    var lostDevices: [LostDevice] = []
    /// Common ancestor locationID of the lost devices when they collapse to
    /// one branch (the grouped-loss finding in data form); nil otherwise.
    var sharedParentLocationID: UInt32?
    /// True when a device with exactly `sharedParentLocationID` was present in
    /// the pre-incident sample — i.e. the shared parent is a real node, not
    /// just a locationID prefix.
    var sharedParentResolved: Bool = false

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
