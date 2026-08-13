import Foundation
import AppKit
import CoreGraphics

/// Writes the connection diagram as an `.excalidraw` document.
///
/// Reuses the same layout engine the window renders, so an export matches
/// exactly what you were looking at — and once open in Excalidraw it can be
/// annotated, rearranged and shared, which is what you actually want when
/// handing a topology to someone else.
enum ExcalidrawExport {

    // Excalidraw's own palette. The app's dark-background tints would be
    // illegible on Excalidraw's white canvas, so each role is remapped to the
    // nearest standard stroke/fill pair rather than carried across verbatim.
    private struct Palette {
        var stroke: String
        var fill: String
    }

    private static func palette(_ kind: TopoNode.Kind) -> Palette {
        switch kind {
        case .powerSource: return Palette(stroke: "#f08c00", fill: "#ffec99")
        case .host:        return Palette(stroke: "#1971c2", fill: "#a5d8ff")
        case .thunderbolt: return Palette(stroke: "#6741d9", fill: "#d0bfff")
        case .hub:         return Palette(stroke: "#e8590c", fill: "#ffd8a8")
        case .device:      return Palette(stroke: "#2f9e44", fill: "#b2f2bb")
        }
    }

    /// Same protocol encoding as the app, remapped to Excalidraw's palette.
    private static func linkStroke(_ p: LinkProtocol) -> String {
        switch p {
        case .power:       return "#f08c00"
        case .thunderbolt: return "#6741d9"
        case .usb3:        return "#1971c2"
        case .usb2:        return "#0c8599"
        case .usbLow:      return "#868e96"
        case .unknown:     return "#adb5bd"
        }
    }
    private static let textColor = "#1e1e1e"

    // MARK: - Document

    static func document(layout: DiagramLayout, caption: String) -> Data? {
        var elements: [[String: Any]] = []
        var index = 0

        // Edges first so boxes paint over the joins.
        for edge in layout.edges {
            guard let first = edge.points.first else { continue }
            let xs = edge.points.map(\.x), ys = edge.points.map(\.y)
            let relative = edge.points.map { [$0.x - first.x, $0.y - first.y] }

            var element = base(id: "edge-\(index)", type: "line", index: index,
                               x: Double(first.x), y: Double(first.y),
                               width: Double((xs.max() ?? 0) - (xs.min() ?? 0)),
                               height: Double((ys.max() ?? 0) - (ys.min() ?? 0)),
                               stroke: linkStroke(edge.linkProtocol),
                               fill: "transparent")
            element["strokeWidth"] = (edge.linkProtocol == .power || edge.linkProtocol == .thunderbolt) ? 2 : 1
            // Dashed carries the same meaning as in the app: tunneled traffic.
            if edge.tunneled { element["strokeStyle"] = "dashed" }
            element["points"] = relative
            element["lastCommittedPoint"] = NSNull()
            element["startBinding"] = NSNull()
            element["endBinding"] = NSNull()
            element["startArrowhead"] = NSNull()
            element["endArrowhead"] = NSNull()
            elements.append(element)
            index += 1
        }

        for placed in layout.nodes {
            let colors = palette(placed.node.kind)
            let frame = placed.frame

            var box = base(id: "box-\(index)", type: "rectangle", index: index,
                           x: Double(frame.minX), y: Double(frame.minY),
                           width: Double(frame.width), height: Double(frame.height),
                           stroke: colors.stroke, fill: colors.fill)
            box["roundness"] = ["type": 3]
            elements.append(box)
            index += 1

            // Free text rather than container-bound: bound text is centred and
            // re-wrapped by Excalidraw, which would undo the deliberate
            // left-aligned title/detail stacking.
            elements.append(text(id: "title-\(index)", index: index,
                                 x: Double(frame.minX) + 12, y: Double(frame.minY) + 10,
                                 width: Double(frame.width) - 24, height: 20,
                                 content: placed.node.title, size: 16))
            index += 1

            let detail = detailLine(for: placed.node, boxWidth: frame.width)
            if !detail.isEmpty {
                elements.append(text(id: "detail-\(index)", index: index,
                                     x: Double(frame.minX) + 12, y: Double(frame.minY) + 31,
                                     width: Double(frame.width) - 24, height: 16,
                                     content: detail, size: 12, color: "#495057"))
                index += 1
            }
        }

        // A caption keeps the export self-describing once it leaves the app.
        elements.append(text(id: "caption-\(index)", index: index,
                             x: Double(24), y: Double(layout.size.height) + 12,
                             width: 700, height: 20,
                             content: caption, size: 14, color: "#868e96"))

        let document: [String: Any] = [
            "type": "excalidraw",
            "version": 2,
            "source": "TBDoctor",
            "elements": elements,
            "appState": ["gridSize": NSNull(), "viewBackgroundColor": "#ffffff"],
            "files": [String: Any]()
        ]

        return try? JSONSerialization.data(withJSONObject: document, options: [.prettyPrinted])
    }

    // MARK: - Detail line

    /// Exported text is free-floating, so anything too long simply runs past the
    /// box edge. Boxes are sized for the on-screen badges, so the location ID is
    /// only appended when it actually fits.
    private static func detailLine(for node: TopoNode, boxWidth: CGFloat) -> String {
        // "60W" and "60W over Thunderbolt" side by side reads as a mistake.
        let badges = node.badges.filter { badge in
            guard let subtitle = node.subtitle else { return true }
            return !subtitle.contains(badge)
        }

        var parts = badges
        if let subtitle = node.subtitle { parts.append(subtitle) }
        let full = parts.joined(separator: "  ·  ")

        let available = Double(boxWidth) - 24
        if measure(full) <= available { return full }

        let withoutSubtitle = badges.joined(separator: "  ·  ")
        return measure(withoutSubtitle) <= available ? withoutSubtitle : node.subtitle ?? withoutSubtitle
    }

    private static func measure(_ string: String) -> Double {
        (string as NSString)
            .size(withAttributes: [.font: NSFont.systemFont(ofSize: 12)]).width
    }

    // MARK: - Elements

    private static func base(id: String, type: String, index: Int,
                             x: Double, y: Double, width: Double, height: Double,
                             stroke: String, fill: String) -> [String: Any] {
        [
            "id": id,
            "type": type,
            "x": x, "y": y, "width": width, "height": height,
            "angle": 0,
            "strokeColor": stroke,
            "backgroundColor": fill,
            "fillStyle": "solid",
            "strokeWidth": 1,
            "strokeStyle": "solid",
            // Excalidraw's default sketchy stroke; the point of exporting here
            // rather than to SVG is that it looks and edits like Excalidraw.
            "roughness": 1,
            "opacity": 100,
            "groupIds": [String](),
            "frameId": NSNull(),
            "roundness": NSNull(),
            "seed": seed(index),
            "version": 1,
            "versionNonce": seed(index &* 31 &+ 7),
            "isDeleted": false,
            "boundElements": NSNull(),
            "updated": 1,
            "link": NSNull(),
            "locked": false
        ]
    }

    private static func text(id: String, index: Int,
                             x: Double, y: Double, width: Double, height: Double,
                             content: String, size: Double, color: String = textColor) -> [String: Any] {
        var element = base(id: id, type: "text", index: index,
                           x: x, y: y, width: width, height: height,
                           stroke: color, fill: "transparent")
        element["text"] = content
        element["originalText"] = content
        element["fontSize"] = size
        // 2 = Helvetica. Excalidraw's hand-drawn default renders hex location
        // IDs and long device names poorly at this size.
        element["fontFamily"] = 2
        element["textAlign"] = "left"
        element["verticalAlign"] = "top"
        element["containerId"] = NSNull()
        element["lineHeight"] = 1.25
        element["autoResize"] = true
        return element
    }

    /// Deterministic so re-exporting the same topology produces a diffable file
    /// rather than churning every element's random seed.
    private static func seed(_ index: Int) -> Int {
        var value = UInt64(truncatingIfNeeded: index &+ 1)
        value = value &* 6364136223846793005 &+ 1442695040888963407
        return Int(value % 2_000_000_000)
    }
}
