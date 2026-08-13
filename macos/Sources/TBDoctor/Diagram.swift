import Foundation
import CoreGraphics
import AppKit
import SwiftUI

// MARK: - Style selection

enum DiagramStyle: String, CaseIterable, Identifiable {
    case cascade, topDown, flow

    var id: String { rawValue }

    var label: String {
        switch self {
        case .cascade: return "Cascade"
        case .topDown: return "Top-down"
        case .flow:    return "Flow"
        }
    }

    var summary: String {
        switch self {
        case .cascade: return "Each child steps down and right. Narrow; grows downward."
        case .topDown: return "Children fan out below. Power enters from the left."
        case .flow:    return "Reads left to right: power, Mac, dock, hubs, devices."
        }
    }
}

// MARK: - Placement results

struct PlacedNode: Identifiable {
    var id: String
    var node: TopoNode
    var frame: CGRect
}

struct DiagramEdge: Identifiable {
    var id: String
    var points: [CGPoint]
    var linkProtocol: LinkProtocol
    /// Drawn dashed: this link's traffic rides a Thunderbolt tunnel.
    var tunneled: Bool = false
}

struct DiagramLayout {
    var nodes: [PlacedNode] = []
    var edges: [DiagramEdge] = []
    var size: CGSize = .zero
}

// MARK: - Metrics

enum DiagramMetrics {
    static let titleFont = NSFont.systemFont(ofSize: 12.5, weight: .semibold)
    static let badgeFont = NSFont.systemFont(ofSize: 10.5)

    static let height: CGFloat = 56
    static let minWidth: CGFloat = 150
    static let maxWidth: CGFloat = 380
    /// Must match NodeBox's real geometry: 8pt padding + 24pt icon + 9pt
    /// spacing, plus slack. SwiftUI renders marginally wider than NSFont
    /// measures, and `lineLimit(1)` truncates on even a fractional overflow —
    /// which is how "61W USB-C Power Adapter" lost its last word.
    static let leading: CGFloat = 45
    static let trailing: CGFloat = 22

    /// Boxes are sized to their text rather than fixed, because truncating
    /// "4-Port USB 2.0 Hub — LG Electronics Inc." to "4-Port USB 2.0 Hub —…"
    /// throws away the one word that identifies the hardware.
    static func width(for node: TopoNode) -> CGFloat {
        let title = (node.title as NSString)
            .size(withAttributes: [.font: titleFont]).width
        let badges = (node.badges.joined(separator: "   ") as NSString)
            .size(withAttributes: [.font: badgeFont]).width
        return min(maxWidth, max(minWidth, leading + max(title, badges) + trailing))
    }
}

// MARK: - Layout engine

enum Diagram {

    static func layout(root: TopoNode, style: DiagramStyle) -> DiagramLayout {
        var result: DiagramLayout
        switch style {
        case .cascade: result = cascade(root)
        case .topDown: result = topDown(root)
        case .flow:    result = flow(root)
        }
        addDisplayLinks(&result, root: root)
        return result
    }

    /// A monitor with a USB hub has two connections: the USB one that puts it in
    /// the tree, and a DisplayPort tunnel carrying its video. The tree can only
    /// express one, so the second is drawn as an extra edge routed clear of the
    /// layout — otherwise half of what that cable does is invisible.
    private static func addDisplayLinks(_ layout: inout DiagramLayout, root: TopoNode) {
        var nearestDock: [String: String] = [:]
        func walk(_ node: TopoNode, dock: String?) {
            if let dock { nearestDock[node.id] = dock }
            let next = node.kind == .thunderbolt ? node.id : dock
            node.children.forEach { walk($0, dock: next) }
        }
        walk(root, dock: nil)

        let frames = Dictionary(layout.nodes.map { ($0.id, $0.frame) }, uniquingKeysWith: { first, _ in first })
        var extra: [DiagramEdge] = []
        var rightmost = layout.size.width

        for placed in layout.nodes
        where placed.node.carriesDisplay && placed.node.linkProtocol != .displayPort {
            guard let dockID = nearestDock[placed.id], let source = frames[dockID] else { continue }
            let target = placed.frame
            let lane = max(source.maxX, target.maxX) + 26
            rightmost = max(rightmost, lane + 12)
            extra.append(DiagramEdge(
                id: "dp-\(placed.id)",
                points: [
                    CGPoint(x: source.maxX, y: source.midY),
                    CGPoint(x: lane, y: source.midY),
                    CGPoint(x: lane, y: target.midY),
                    CGPoint(x: target.maxX, y: target.midY)
                ],
                linkProtocol: .displayPort,
                tunneled: true))
        }

        layout.edges.append(contentsOf: extra)
        layout.size.width = rightmost
    }

    /// Power edges are the ones feeding the host — drawn distinctly so the
    /// power path never gets confused with the data tree.
    private static func kind(childIsHost: Bool) -> LinkProtocol { childIsHost ? .power : .unknown }

    // MARK: Cascade

    private static func cascade(_ root: TopoNode) -> DiagramLayout {
        let indent: CGFloat = 44, vGap: CGFloat = 18, stem: CGFloat = 18
        var result = DiagramLayout()
        var y: CGFloat = 0

        func walk(_ node: TopoNode, depth: Int, parent: CGRect?) {
            let frame = CGRect(x: CGFloat(depth) * indent, y: y,
                               width: DiagramMetrics.width(for: node),
                               height: DiagramMetrics.height)
            y += DiagramMetrics.height + vGap
            result.nodes.append(PlacedNode(id: node.id, node: node, frame: frame))

            if let parent {
                // Each child drops its own stem from the parent's underside;
                // overlapping stems read as one continuous rail.
                let x = parent.minX + stem
                result.edges.append(DiagramEdge(id: node.id, points: [
                    CGPoint(x: x, y: parent.maxY),
                    CGPoint(x: x, y: frame.midY),
                    CGPoint(x: frame.minX, y: frame.midY)
                ], linkProtocol: node.linkProtocol, tunneled: node.isTunneled))
            }

            for child in node.children { walk(child, depth: depth + 1, parent: frame) }
        }

        walk(root, depth: 0, parent: nil)
        return finish(result)
    }

    // MARK: Top-down

    private static func topDown(_ root: TopoNode) -> DiagramLayout {
        let hGap: CGFloat = 20, vGap: CGFloat = 48, powerGap: CGFloat = 64
        var result = DiagramLayout()

        // The host subtree is laid out normally; the supply is placed beside it
        // so the power path reads as entering from the side rather than being
        // another link in the data chain.
        guard let host = root.children.first else { return single(root) }

        var cursor: CGFloat = 0
        var links: [(TopoNode, CGRect, CGRect)] = []

        @discardableResult
        func place(_ node: TopoNode, depth: Int) -> CGRect {
            let width = DiagramMetrics.width(for: node)
            let y = CGFloat(depth) * (DiagramMetrics.height + vGap)

            if node.children.isEmpty {
                let frame = CGRect(x: cursor, y: y, width: width, height: DiagramMetrics.height)
                cursor += width + hGap
                result.nodes.append(PlacedNode(id: node.id, node: node, frame: frame))
                return frame
            }

            let childFrames = node.children.map { place($0, depth: depth + 1) }
            let midX = (childFrames.first!.midX + childFrames.last!.midX) / 2
            let frame = CGRect(x: midX - width / 2, y: y, width: width, height: DiagramMetrics.height)
            result.nodes.append(PlacedNode(id: node.id, node: node, frame: frame))
            for (child, childFrame) in zip(node.children, childFrames) {
                links.append((child, frame, childFrame))
            }
            return frame
        }

        let hostFrame = place(host, depth: 0)

        // Directly beside the host. Placing it left of the whole tree instead
        // stretches the power edge across the entire diagram and pushes the Mac
        // off screen — the short adjacent link reads far better.
        let powerWidth = DiagramMetrics.width(for: root)
        let powerFrame = CGRect(x: hostFrame.minX - powerWidth - powerGap, y: hostFrame.minY,
                                width: powerWidth, height: DiagramMetrics.height)
        result.nodes.append(PlacedNode(id: root.id, node: root, frame: powerFrame))
        result.edges.append(DiagramEdge(id: "power", points: [
            CGPoint(x: powerFrame.maxX, y: powerFrame.midY),
            CGPoint(x: hostFrame.minX, y: hostFrame.midY)
        ], linkProtocol: .power))

        for (child, parent, childFrame) in links {
            let midY = parent.maxY + vGap / 2
            result.edges.append(DiagramEdge(id: child.id, points: [
                CGPoint(x: parent.midX, y: parent.maxY),
                CGPoint(x: parent.midX, y: midY),
                CGPoint(x: childFrame.midX, y: midY),
                CGPoint(x: childFrame.midX, y: childFrame.minY)
            ], linkProtocol: child.linkProtocol, tunneled: child.isTunneled))
        }

        return finish(result)
    }

    // MARK: Flow

    private static func flow(_ root: TopoNode) -> DiagramLayout {
        let hGap: CGFloat = 62, vGap: CGFloat = 14
        var result = DiagramLayout()

        // Columns are as wide as their widest member, so boxes line up rather
        // than staggering with variable text lengths.
        var widestAtDepth: [Int: CGFloat] = [:]
        func measure(_ node: TopoNode, _ depth: Int) {
            widestAtDepth[depth] = max(widestAtDepth[depth] ?? 0, DiagramMetrics.width(for: node))
            node.children.forEach { measure($0, depth + 1) }
        }
        measure(root, 0)

        var columnX: [Int: CGFloat] = [:]
        var x: CGFloat = 0
        for depth in widestAtDepth.keys.sorted() {
            columnX[depth] = x
            x += widestAtDepth[depth]! + hGap
        }

        var cursor: CGFloat = 0

        @discardableResult
        func place(_ node: TopoNode, depth: Int) -> CGRect {
            let width = DiagramMetrics.width(for: node)
            let x = columnX[depth] ?? 0

            if node.children.isEmpty {
                let frame = CGRect(x: x, y: cursor, width: width, height: DiagramMetrics.height)
                cursor += DiagramMetrics.height + vGap
                result.nodes.append(PlacedNode(id: node.id, node: node, frame: frame))
                return frame
            }

            let childFrames = node.children.map { place($0, depth: depth + 1) }
            let midY = (childFrames.first!.midY + childFrames.last!.midY) / 2
            let frame = CGRect(x: x, y: midY - DiagramMetrics.height / 2,
                               width: width, height: DiagramMetrics.height)
            result.nodes.append(PlacedNode(id: node.id, node: node, frame: frame))

            for (child, childFrame) in zip(node.children, childFrames) {
                let midX = frame.maxX + hGap / 2
                result.edges.append(DiagramEdge(id: child.id, points: [
                    CGPoint(x: frame.maxX, y: frame.midY),
                    CGPoint(x: midX, y: frame.midY),
                    CGPoint(x: midX, y: childFrame.midY),
                    CGPoint(x: childFrame.minX, y: childFrame.midY)
                ], linkProtocol: child.linkProtocol, tunneled: child.isTunneled))
            }
            return frame
        }

        place(root, depth: 0)
        return finish(result)
    }

    // MARK: Helpers

    private static func single(_ node: TopoNode) -> DiagramLayout {
        var result = DiagramLayout()
        result.nodes = [PlacedNode(id: node.id, node: node,
                                   frame: CGRect(x: 0, y: 0,
                                                 width: DiagramMetrics.width(for: node),
                                                 height: DiagramMetrics.height))]
        return finish(result)
    }

    /// Shifts everything to the origin with a margin and records total size.
    private static func finish(_ input: DiagramLayout) -> DiagramLayout {
        var result = input
        let margin: CGFloat = 24
        guard !result.nodes.isEmpty else { return result }

        let minX = min(result.nodes.map(\.frame.minX).min() ?? 0,
                       result.edges.flatMap(\.points).map(\.x).min() ?? 0)
        let minY = min(result.nodes.map(\.frame.minY).min() ?? 0,
                       result.edges.flatMap(\.points).map(\.y).min() ?? 0)
        let dx = margin - minX, dy = margin - minY

        result.nodes = result.nodes.map {
            PlacedNode(id: $0.id, node: $0.node, frame: $0.frame.offsetBy(dx: dx, dy: dy))
        }
        result.edges = result.edges.map {
            DiagramEdge(id: $0.id,
                        points: $0.points.map { CGPoint(x: $0.x + dx, y: $0.y + dy) },
                        linkProtocol: $0.linkProtocol, tunneled: $0.tunneled)
        }
        result.size = CGSize(width: (result.nodes.map(\.frame.maxX).max() ?? 0) + margin,
                             height: (result.nodes.map(\.frame.maxY).max() ?? 0) + margin)
        return result
    }
}

// MARK: - Shared visual vocabulary

enum TopoStyle {
    static func tint(_ kind: TopoNode.Kind) -> Color {
        switch kind {
        case .powerSource: return .yellow
        case .host:        return .blue
        case .thunderbolt: return .purple
        case .hub:         return .orange
        case .device:      return .teal
        case .display:     return .pink
        }
    }

    static func symbol(_ kind: TopoNode.Kind) -> String {
        switch kind {
        case .powerSource: return "bolt.fill"
        case .host:        return "laptopcomputer"
        case .thunderbolt: return "cable.connector.horizontal"
        case .hub:         return "point.3.filled.connected.trianglepath.dotted"
        case .device:      return "circle.hexagongrid.fill"
        case .display:     return "display"
        }
    }

    static let legendItems: [(label: String, color: Color)] = [
        ("power source", .yellow), ("Mac", .blue), ("Thunderbolt", .purple),
        ("hub (consumer)", .orange), ("device", .teal), ("display", .pink)
    ]

    static let rail = Color.secondary.opacity(0.45)

    /// Edges are coloured by what the link actually carries.
    static func color(_ p: LinkProtocol) -> Color {
        switch p {
        case .power:       return .yellow
        case .thunderbolt: return .purple
        case .displayPort: return .pink
        case .usb3:        return .blue
        case .usb2:        return .teal
        case .usbLow:      return .gray
        case .unknown:     return .secondary
        }
    }

    static func width(_ p: LinkProtocol) -> CGFloat {
        switch p {
        case .power, .thunderbolt, .displayPort: return 2.4
        case .usb3:                return 2.0
        default:                   return 1.4
        }
    }
}
