import Foundation

/// A node in the physical connection tree.
struct TopoNode: Identifiable {
    enum Kind {
        case powerSource   // where power enters the system
        case host          // the Mac
        case thunderbolt   // a Thunderbolt device (dock)
        case hub           // a USB hub — has children
        case device        // a leaf peripheral
    }

    var id: String
    var kind: Kind
    var title: String
    var subtitle: String?
    /// Short annotations rendered as chips: link speed, power role, and so on.
    var badges: [String] = []
    /// The thing this node explains — why it matters, in one line.
    var note: String?
    var children: [TopoNode] = []
}

/// Builds the connection tree from a sample.
///
/// The point of this is answering two questions that are genuinely hard to see
/// in `ioreg` output or System Information: **where does power actually enter**,
/// and **what is sitting behind what**. Both were misread during the
/// investigation that produced this tool.
enum Topology {

    static func build(from sample: Sample) -> TopoNode {
        var root = powerNode(sample)
        var host = TopoNode(
            id: "host",
            kind: .host,
            title: "This Mac",
            subtitle: nil,
            badges: sample.externalConnected ? ["on AC"] : ["on battery"])

        // USB devices, indexed so children can find parents by location prefix.
        let byLocation = Dictionary(sample.usb.map { ($0.locationID, $0) }, uniquingKeysWith: { a, _ in a })
        var childrenOf: [UInt32: [USBDevice]] = [:]
        var roots: [USBDevice] = []

        for device in sample.usb {
            // Walk up until we find an ancestor that is itself an enumerated
            // device; anything above that is a bare controller, not a peer.
            var parent = device.parentLocationID
            var attached = false
            while let candidate = parent {
                if let owner = byLocation[candidate] {
                    childrenOf[owner.locationID, default: []].append(device)
                    attached = true
                    break
                }
                parent = USBDevice(name: "", speed: 0, locationID: candidate).parentLocationID
            }
            if !attached { roots.append(device) }
        }

        func usbNode(_ device: USBDevice) -> TopoNode {
            let kids = (childrenOf[device.locationID] ?? []).sorted { $0.locationID < $1.locationID }
            var node = TopoNode(
                id: String(device.locationID),
                kind: kids.isEmpty ? .device : .hub,
                title: displayName(for: device, children: kids),
                subtitle: String(format: "0x%08X", device.locationID),
                badges: [device.speedLabel])

            if !kids.isEmpty {
                // Every USB hub downstream of the host is a power *consumer*.
                // Spelling this out is the entire point: a monitor with a
                // built-in hub looks like infrastructure and gets mistaken for
                // one, but it cannot supply the host with anything.
                node.badges.append("hub · \(kids.count)")
                node.note = "Downstream — draws power, supplies none to the Mac."
            }
            node.children = kids.map(usbNode)
            return node
        }

        // Thunderbolt devices sit between the host and the USB tree.
        if let dock = sample.tb.first {
            var dockNode = TopoNode(
                id: dock.uid,
                kind: .thunderbolt,
                title: dock.label,
                subtitle: "Thunderbolt · route \(dock.route)",
                badges: dock.linkGbps.map { [String(format: "%.0f Gb/s", $0)] } ?? [])
            if sample.adapter.looksLikeDock {
                dockNode.badges.append("supplying host power")
                dockNode.note = "Carrying power *and* data on one cable — a power shortfall here takes the data link down with it."
            } else {
                dockNode.note = "Data only — the Mac is powered separately, so a power dip cannot reset this link."
            }
            dockNode.children = roots.sorted { $0.locationID < $1.locationID }.map(usbNode)
            host.children = [dockNode]
        } else {
            host.children = roots.sorted { $0.locationID < $1.locationID }.map(usbNode)
        }

        root.children = [host]
        return root
    }

    // MARK: - Naming

    /// Hubs frequently report themselves as "Generic". When a hub's vendor ID
    /// matches one of its own children, that child's name identifies the
    /// hardware the hub is built into — which is how an anonymous
    /// "4-Port USB 2.0 Hub" resolves to a monitor.
    static func displayName(for device: USBDevice, children: [USBDevice]) -> String {
        let generic = ["generic", "", "unknown"]
        let vendorIsVague = generic.contains((device.vendorName ?? "").lowercased())
        guard vendorIsVague, let vid = device.vendorID else { return device.name }

        if let sibling = children.first(where: { $0.vendorID == vid && !generic.contains(($0.vendorName ?? "").lowercased()) }) {
            let vendor = sibling.vendorName ?? ""
            return "\(device.name) — \(vendor)"
        }
        return device.name
    }

    // MARK: - Power

    private static func powerNode(_ sample: Sample) -> TopoNode {
        guard sample.adapter.isPresent else {
            return TopoNode(
                id: "power",
                kind: .powerSource,
                title: "No adapter",
                subtitle: "running on battery",
                badges: [],
                note: "Nothing is supplying power.")
        }

        let watts = sample.adapter.watts ?? 0
        if sample.adapter.looksLikeDock {
            return TopoNode(
                id: "power",
                kind: .powerSource,
                title: "Dock (unidentified supply)",
                subtitle: "\(watts)W over Thunderbolt",
                badges: ["\(watts)W"],
                note: "Power is coming from the dock, over the same cable as your data.")
        }

        return TopoNode(
            id: "power",
            kind: .powerSource,
            title: sample.adapter.name?.trimmingCharacters(in: .whitespaces) ?? "Power adapter",
            subtitle: sample.adapter.manufacturer,
            badges: ["\(watts)W"],
            note: "Power enters here, on its own cable — independent of the data link.")
    }
}
