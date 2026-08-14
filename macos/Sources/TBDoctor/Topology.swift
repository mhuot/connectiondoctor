import Foundation

/// What a link actually carries. Colouring edges by this makes the single most
/// important fact about a topology visible at a glance — that everything is
/// stuck on USB 2.0, for instance, which took a lot of squinting to notice.
enum LinkProtocol: String, Codable, CaseIterable {
    case power, thunderbolt, displayPort, usb3, usb2, usbLow, unknown

    var label: String {
        switch self {
        case .power:       return "power"
        case .thunderbolt: return "Thunderbolt / USB4"
        case .displayPort: return "DisplayPort"
        case .usb3:        return "USB 3.x"
        case .usb2:        return "USB 2.0"
        case .usbLow:      return "USB 1.x"
        case .unknown:     return "unknown"
        }
    }
}

/// Whether to show every enumerated node, or only the boxes you could point at.
enum TopoMode: String, CaseIterable, Identifiable {
    case physical, full
    var id: String { rawValue }
    var label: String { self == .physical ? "Physical" : "Physical + logical" }
    var summary: String {
        self == .physical
            ? "Only real enclosures. Internal hubs and control interfaces fold into the box they live in."
            : "Every enumerated node, including internal hubs and control interfaces."
    }
}

/// One labelled fact about a node, shown in the inspector.
struct NodeDetail: Identifiable, Hashable {
    var label: String
    var value: String
    /// Marks the values worth searching the web for — in practice, VID:PID.
    var searchable: Bool = false
    var id: String { label }
}

/// A node in the physical connection tree.
struct TopoNode: Identifiable {
    enum Kind {
        case powerSource   // where power enters the system
        case host          // the Mac
        case thunderbolt   // a Thunderbolt device (dock)
        case hub           // a USB hub — has children
        case device        // a leaf peripheral
        case display       // a monitor
    }

    var id: String
    var kind: Kind
    var title: String
    var subtitle: String?
    /// Short annotations rendered as chips: link speed, power role, and so on.
    var badges: [String] = []
    /// The thing this node explains — why it matters, in one line.
    var note: String?
    /// Everything known about this node, for the inspector.
    var details: [NodeDetail] = []
    /// VID:PID when the node is a USB device, for the lookup action.
    var vidPid: String?
    /// Protocol of the link *into* this node, used to colour its edge.
    var linkProtocol: LinkProtocol = .unknown
    /// USB vendor ID, used to decide what belongs to the same physical box.
    var vendorID: Int?
    /// How many enumerated nodes folded into this one in physical mode.
    var internalCount: Int = 0
    /// Set when this box also has a DisplayPort connection, which is a separate
    /// tunnel from its USB one. A monitor with a built-in hub has both, and
    /// showing only the USB side hides half of what the cable is doing.
    var carriesDisplay: Bool = false
    /// True when this link is genuinely *tunneled* over Thunderbolt/USB4.
    ///
    /// USB4 multiplexes independent tunnels — DisplayPort, USB 3.x and PCIe —
    /// onto one link. USB 2.0 is NOT among them: it is carried on the Type-C
    /// cable's dedicated D+/D- pair and passed through each hub, so marking it
    /// tunneled would be wrong.
    var isTunneled: Bool = false
    var children: [TopoNode] = []
}

/// Builds the connection tree from a sample.
///
/// The point of this is answering two questions that are genuinely hard to see
/// in `ioreg` output or System Information: **where does power actually enter**,
/// and **what is sitting behind what**. Both were misread during the
/// investigation that produced this tool.
enum Topology {

    static func build(from sample: Sample, mode: TopoMode = .full) -> TopoNode {
        var tree = mode == .full ? buildFull(from: sample) : collapse(buildFull(from: sample))
        // Must run on the *final* tree: in physical mode a daisy-chained dock
        // only receives its USB subtree during the collapse, so summarising
        // earlier would credit every tunnel to the upstream dock.
        summariseTunnels(&tree)
        return tree
    }

    /// VIDs belonging to hub-controller silicon rather than to a product you
    /// could point at. A "USB2.0 Hub" from Intel or Genesys is always *inside*
    /// something else, so in physical mode it folds into its enclosure.
    private static let controllerSilicon: Set<Int> = [
        0x8087,  // Intel
        0x05E3,  // Genesys Logic
        0x1D5C,  // Fresco Logic
        0x2109,  // VIA Labs
        0x1A40   // Terminus
    ]

    /// Reduces the tree to physical enclosures.
    ///
    /// Two things get folded: descendants sharing their enclosure's vendor ID
    /// (a dock's own hubs and control interfaces), and bare controller silicon.
    /// Siblings resolving to the same vendor are merged, which is what stops a
    /// dock appearing twice — once for its USB 2 tree and once for its USB 3 one.
    private static func collapse(_ root: TopoNode) -> TopoNode {
        func merge(_ nodes: [TopoNode]) -> [TopoNode] {
            var order: [Int] = []
            var grouped: [Int: TopoNode] = [:]
            var ungrouped: [TopoNode] = []

            for node in nodes {
                guard let vendor = node.vendorID else { ungrouped.append(node); continue }
                if var existing = grouped[vendor] {
                    existing.children += node.children
                    existing.internalCount += 1 + node.internalCount
                    // Prefer whichever link is faster — a box reached over both
                    // USB 2 and USB 3 is really reached over USB 3.
                    if node.linkProtocol == .usb3 { existing.linkProtocol = .usb3 }
                    grouped[vendor] = existing
                } else {
                    grouped[vendor] = node
                    order.append(vendor)
                }
            }
            return order.compactMap { grouped[$0] } + ungrouped
        }

        func physicalise(_ node: TopoNode) -> TopoNode {
            var result = node
            var folded = node.internalCount
            var surfaced: [TopoNode] = []

            func gather(_ current: TopoNode) {
                for child in current.children {
                    // A Thunderbolt device is always its own enclosure. It also
                    // reports its *controller silicon* vendor (Intel on both of
                    // these docks), so vendor-based folding would swallow it.
                    if child.kind == .thunderbolt { surfaced.append(child); continue }
                    let sameBox = child.vendorID != nil && child.vendorID == node.vendorID
                    let isSilicon = child.vendorID.map { controllerSilicon.contains($0) } ?? false
                    if sameBox || isSilicon {
                        folded += 1 + child.internalCount
                        gather(child)
                    } else {
                        surfaced.append(child)
                    }
                }
            }
            gather(node)

            result.internalCount = folded
            if folded > 0 { result.badges.append("+\(folded) internal") }
            result.children = merge(surfaced).map(physicalise)
            return result
        }

        return reparentToDocks(physicalise(root))
    }

    /// Moves a dock's USB subtree under the dock's own Thunderbolt node.
    ///
    /// When a dock is daisy-chained, its USB hub enumerates *beneath the
    /// upstream dock's* hub, because the traffic is tunneled. That is true at
    /// the USB layer but misleading as a picture of the desk: those peripherals
    /// are plugged into the downstream dock. Physical mode says so.
    private static func reparentToDocks(_ root: TopoNode) -> TopoNode {
        var docks: [(id: String, brand: String)] = []
        func findDocks(_ node: TopoNode) {
            if node.kind == .thunderbolt,
               let vendor = node.details.first(where: { $0.label == "Vendor" })?.value {
                let brand = vendor.split(separator: ",").first.map(String.init)?
                    .trimmingCharacters(in: .whitespaces) ?? vendor
                if !brand.isEmpty { docks.append((node.id, brand)) }
            }
            node.children.forEach(findDocks)
        }
        findDocks(root)
        guard !docks.isEmpty else { return root }

        var moved: [String: [TopoNode]] = [:]

        /// Detaches any USB subtree belonging to a dock other than the one it
        /// currently sits under.
        func detach(_ node: TopoNode, enclosingDock: String?) -> TopoNode {
            var result = node
            var kept: [TopoNode] = []
            for child in node.children {
                let childDock = child.kind == .thunderbolt ? child.id : enclosingDock
                let processed = detach(child, enclosingDock: childDock)
                // Only USB subtrees move. Without this the host itself matches
                // ("its subtree contains a Microsoft device") and gets relocated
                // inside a dock that lives beneath it, collapsing the tree.
                let movable = processed.kind == .hub || processed.kind == .device
                if movable,
                   let owner = docks.first(where: { $0.id != enclosingDock && subtreeMentions(processed, brand: $0.brand) }) {
                    moved[owner.id, default: []].append(processed)
                } else {
                    kept.append(processed)
                }
            }
            result.children = kept
            return result
        }

        var pruned = detach(root, enclosingDock: nil)

        func attach(_ node: inout TopoNode) {
            if let extras = moved[node.id] { node.children.append(contentsOf: extras) }
            for index in node.children.indices { attach(&node.children[index]) }
        }
        attach(&pruned)
        return pruned
    }

    private static func buildFull(from sample: Sample) -> TopoNode {
        var root = powerNode(sample)
        var host = TopoNode(
            id: "host",
            kind: .host,
            title: "This Mac",
            subtitle: nil,
            badges: sample.isDesktop ? ["mains"] : (sample.externalConnected ? ["on AC"] : ["on battery"]))
        host.linkProtocol = .power
        host.details = sample.isDesktop
            ? [
                NodeDetail(label: "Power", value: "mains (desktop, no battery)"),
                NodeDetail(label: "USB devices", value: String(sample.usb.count))
            ]
            : [
                NodeDetail(label: "External power", value: sample.externalConnected ? "connected" : "not connected"),
                NodeDetail(label: "Battery", value: "\(sample.percent)%"),
                NodeDetail(label: "Battery current", value: String(format: "%d mA  (%.1f W)", sample.amperageMilliAmps, sample.batteryWatts)),
                NodeDetail(label: "Battery voltage", value: String(format: "%.2f V", sample.voltage)),
                NodeDetail(label: "USB devices", value: String(sample.usb.count))
            ]

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
            node.details = details(for: device)
            node.vidPid = device.vidPid
            node.vendorID = device.vendorID
            switch device.speed {
            case 3, 4: node.linkProtocol = .usb3
            case 2:    node.linkProtocol = .usb2
            case 0, 1: node.linkProtocol = .usbLow
            default:   node.linkProtocol = .unknown
            }

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

        // Build the full Thunderbolt chain. Only placing the first device used
        // to hide daisy-chained docks entirely — their USB subtrees appeared
        // orphaned under the upstream dock, with no sign of the 40 Gb/s link
        // between them.
        func tbNode(_ dock: TBDevice) -> TopoNode {
            var node = TopoNode(
                id: dock.uid,
                kind: .thunderbolt,
                title: dock.label,
                subtitle: "Thunderbolt · route \(dock.route)",
                badges: dock.linkGbps.map { [String(format: "%.0f Gb/s", $0)] } ?? [])
            node.linkProtocol = .thunderbolt
            // Deliberately not dock.vendorID: that is the Thunderbolt controller
            // silicon (Intel on both of these docks), not the product vendor.
            node.vendorID = nil
            node.details = [
                NodeDetail(label: "Vendor", value: dock.vendor),
                NodeDetail(label: "Model", value: dock.model),
                NodeDetail(label: "Link", value: dock.linkGbps.map { String(format: "%.0f Gb/s", $0) } ?? "unknown"),
                NodeDetail(label: "Route string", value: String(dock.route)),
                NodeDetail(label: "Depth", value: String(dock.depth)),
                NodeDetail(label: "UID", value: dock.uid)
            ]
            return node
        }

        let usbRoots = roots.sorted { $0.locationID < $1.locationID }.map(usbNode)

        if !sample.tb.isEmpty {
            let chain = sample.tb.sorted { $0.depth < $1.depth }
            var nodes = chain.map(tbNode)

            // Each dock claims the USB subtrees whose vendor matches its own;
            // whatever is left belongs to the head of the chain.
            var claimed = Set<String>()
            for index in nodes.indices {
                let brand = chain[index].vendor
                    .split(separator: ",").first.map(String.init)?
                    .trimmingCharacters(in: .whitespaces) ?? chain[index].vendor
                guard !brand.isEmpty else { continue }
                let mine = usbRoots.filter { root in
                    !claimed.contains(root.id) && subtreeMentions(root, brand: brand)
                }
                mine.forEach { claimed.insert($0.id) }
                nodes[index].children.append(contentsOf: mine)
            }
            let unclaimed = usbRoots.filter { !claimed.contains($0.id) }
            nodes[0].children.append(contentsOf: unclaimed)

            // Only the protocols USB4 actually tunnels get marked.
            func markTunneled(_ node: inout TopoNode) {
                for index in node.children.indices {
                    switch node.children[index].linkProtocol {
                    case .usb3, .displayPort: node.children[index].isTunneled = true
                    default:                  node.children[index].isTunneled = false
                    }
                    markTunneled(&node.children[index])
                }
            }
            for index in nodes.indices { markTunneled(&nodes[index]) }

            // Nest deeper docks inside shallower ones.
            for index in stride(from: nodes.count - 1, through: 1, by: -1) {
                let child = nodes[index]
                nodes[index - 1].children.insert(child, at: 0)
            }

            if sample.adapter.looksLikeDock {
                nodes[0].badges.append("supplying host power")
                nodes[0].note = "Carrying power *and* data on one cable — a power shortfall here takes the data link down with it."
            } else {
                nodes[0].note = "Data only — the Mac is powered separately, so a power dip cannot reset this link."
            }
            host.children = [nodes[0]]
        } else {
            host.children = usbRoots
        }

        attachDisplays(sample, host: &host)
        root.children = [host]
        return root
    }

    /// True when this subtree contains a device whose vendor string names the
    /// given brand — how a dock's own USB hubs are recognised as belonging to it.
    private static func subtreeMentions(_ node: TopoNode, brand: String) -> Bool {
        if node.details.contains(where: { $0.label == "Vendor" && $0.value.localizedCaseInsensitiveContains(brand) }) {
            return true
        }
        return node.children.contains { subtreeMentions($0, brand: brand) }
    }

    /// Labels each Thunderbolt node with the tunnels riding its link.
    ///
    /// Marking individual edges is not enough on its own: USB 2.0 is carried
    /// natively rather than tunneled, so once that is drawn honestly most of the
    /// tree is undashed and it stops being obvious that the whole subtree
    /// depends on one Thunderbolt link. This says so directly.
    private static func summariseTunnels(_ node: inout TopoNode) {
        for index in node.children.indices { summariseTunnels(&node.children[index]) }
        guard node.kind == .thunderbolt else { return }

        var usb3 = false, usb2 = false, displays = 0
        func scan(_ current: TopoNode) {
            for child in current.children {
                switch child.linkProtocol {
                case .usb3: usb3 = true
                case .usb2, .usbLow: usb2 = true
                default: break
                }
                if child.kind == .display || child.carriesDisplay { displays += 1 }
                scan(child)
            }
        }
        scan(node)

        var carried: [String] = []
        if displays > 0 { carried.append(displays == 1 ? "DP" : "DP ×\(displays)") }
        if usb3 { carried.append("USB3") }
        if !carried.isEmpty { node.badges.append("tunnels: " + carried.joined(separator: " + ")) }
        if usb2 { node.badges.append("USB2 native") }
    }

    // MARK: - Displays

    /// Merges each display into the box it belongs to.
    ///
    /// A monitor with a USB hub is already in the tree as that hub; adding a
    /// separate node for its panel would draw one physical monitor twice. So
    /// where the brand matches, the hub node *becomes* the monitor and gains a
    /// DisplayPort connection alongside its USB one.
    private static func attachDisplays(_ sample: Sample, host: inout TopoNode) {
        guard sample.displaysKnown else { return }

        func displayNode(_ display: DisplayInfo) -> TopoNode {
            var node = TopoNode(id: "display-\(display.id)", kind: .display,
                                title: display.name, subtitle: nil,
                                badges: [display.resolution])
            if let hz = display.refreshHz { node.badges.append(String(format: "%.0f Hz", hz)) }
            node.linkProtocol = .displayPort
            node.carriesDisplay = true
            node.details = displayDetails(display)
            return node
        }

        for display in sample.displays where !display.isBuiltIn {
            let brand = display.name.split(separator: " ").first.map(String.init) ?? display.name
            var merged = false

            func mergeInto(_ node: inout TopoNode) {
                if !merged, brand.count >= 2, subtreeMentions(node, brand: brand) || node.title.localizedCaseInsensitiveContains(brand) {
                    // Only merge into the box itself, not an ancestor that
                    // merely contains it.
                    if node.title.localizedCaseInsensitiveContains(brand)
                        || node.details.contains(where: { $0.label == "Vendor" && $0.value.localizedCaseInsensitiveContains(brand) }) {
                        node.title = display.name
                        node.kind = .display
                        node.carriesDisplay = true
                        node.badges.insert(display.resolution, at: 0)
                        node.details.append(contentsOf: displayDetails(display))
                        merged = true
                        return
                    }
                }
                for index in node.children.indices { mergeInto(&node.children[index]) }
            }
            mergeInto(&host)

            // No USB presence — a display-only monitor. Hang it off the nearest
            // Thunderbolt device, which is what carries its video.
            if !merged {
                func attachToDock(_ node: inout TopoNode) -> Bool {
                    if node.kind == .thunderbolt {
                        // Only here is the video genuinely tunneled; a display
                        // hanging straight off the Mac is DP alt mode or HDMI.
                        var attached = displayNode(display)
                        attached.isTunneled = true
                        node.children.append(attached)
                        return true
                    }
                    for index in node.children.indices where attachToDock(&node.children[index]) { return true }
                    return false
                }
                if !attachToDock(&host) { host.children.append(displayNode(display)) }
            }
        }

        for display in sample.displays where display.isBuiltIn {
            host.children.insert(displayNode(display), at: 0)
        }
    }

    private static func displayDetails(_ display: DisplayInfo) -> [NodeDetail] {
        var rows = [NodeDetail(label: "Display", value: display.name),
                    NodeDetail(label: "Resolution", value: display.resolution)]
        if let hz = display.refreshHz { rows.append(NodeDetail(label: "Refresh", value: String(format: "%.2f Hz", hz))) }
        rows.append(NodeDetail(label: "Connection", value: display.isBuiltIn ? "built-in" : "DisplayPort"))
        if let v = display.vendorNumber { rows.append(NodeDetail(label: "EDID vendor", value: String(format: "0x%04X", v))) }
        if let m = display.modelNumber { rows.append(NodeDetail(label: "EDID model", value: String(format: "0x%04X", m))) }
        return rows
    }

    // MARK: - Details

    /// Everything IOKit publishes about a USB device. The point is research:
    /// when a hub calls itself "USB2.0 Hub", the vendor and product IDs are the
    /// only things that actually identify it.
    static func details(for device: USBDevice) -> [NodeDetail] {
        var rows: [NodeDetail] = []
        func add(_ label: String, _ value: String?, searchable: Bool = false) {
            guard let value, !value.isEmpty else { return }
            rows.append(NodeDetail(label: label, value: value, searchable: searchable))
        }

        add("Product", device.name)
        add("Vendor", device.vendorName)
        if let vidPid = device.vidPid { add("VID:PID", vidPid, searchable: true) }
        if let vendorID = device.vendorID { add("Vendor ID", String(format: "0x%04X  (%d)", vendorID, vendorID)) }
        if let productID = device.productID { add("Product ID", String(format: "0x%04X  (%d)", productID, productID)) }
        add("Serial", device.serial, searchable: false)
        add("Class", USBDevice.className(device.deviceClass))
        if let sub = device.deviceSubClass { add("Subclass", String(format: "0x%02X", sub)) }
        if let proto = device.deviceProtocol { add("Protocol", String(format: "0x%02X", proto)) }
        add("USB version", USBDevice.bcdString(device.usbVersionBCD))
        add("Device release", USBDevice.bcdString(device.releaseBCD))
        add("Negotiated speed", device.speedLabel)
        if let bits = device.linkSpeedBitsPerSecond {
            add("Link rate", String(format: "%.0f Mb/s", Double(bits) / 1_000_000))
        }
        add("Location ID", String(format: "0x%08X", device.locationID))
        if let address = device.usbAddress { add("USB address", String(address)) }
        return rows
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
            if sample.isDesktop {
                return TopoNode(
                    id: "power",
                    kind: .powerSource,
                    title: "Mains power",
                    subtitle: "desktop — no battery",
                    badges: [],
                    note: "Powered directly from the wall; no adapter or battery telemetry exists on this machine.")
            }
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
