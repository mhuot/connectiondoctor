import SwiftUI

struct TopologyView: View {
    let sample: Sample

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // A ScrollView sizes its content to that content's ideal height and
            // then centres it, so `maxHeight: .infinity` does not pin a short
            // tree to the top. Forcing the content to be at least as tall as the
            // viewport, aligned top-leading, does.
            GeometryReader { geometry in
                ScrollView([.vertical, .horizontal]) {
                    NodeView(node: Topology.build(from: sample), isLast: true, ancestorsLast: [])
                        .padding(20)
                        .frame(minWidth: geometry.size.width,
                               minHeight: geometry.size.height,
                               alignment: .topLeading)
                }
            }
            Divider()
            legend
        }
    }

    /// Deliberately outside the ScrollView: inside a horizontally scrolling
    /// container, text does not wrap — it just extends past the pane edge and
    /// gets clipped by the divider.
    private var legend: some View {
        HStack(alignment: .top, spacing: 14) {
            ForEach(TopoStyle.legendItems, id: \.label) { item in
                HStack(spacing: 5) {
                    Circle().fill(item.color).frame(width: 7, height: 7)
                    Text(item.label).font(.caption2).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }
}

// MARK: - Styling

enum TopoStyle {
    static func tint(_ kind: TopoNode.Kind) -> Color {
        switch kind {
        case .powerSource: return .yellow
        case .host:        return .blue
        case .thunderbolt: return .purple
        case .hub:         return .orange
        case .device:      return .teal
        }
    }

    static func symbol(_ kind: TopoNode.Kind) -> String {
        switch kind {
        case .powerSource: return "bolt.fill"
        case .host:        return "laptopcomputer"
        case .thunderbolt: return "cable.connector.horizontal"
        case .hub:         return "point.3.filled.connected.trianglepath.dotted"
        case .device:      return "circle.hexagongrid.fill"
        }
    }

    static let legendItems: [(label: String, color: Color)] = [
        ("power source", .yellow), ("Mac", .blue), ("Thunderbolt", .purple),
        ("hub (consumer)", .orange), ("device", .teal)
    ]

    static let rail = Color.secondary.opacity(0.35)
    /// Vertical distance from the top of a row to the centre of its card's
    /// first line — where the elbow's horizontal stub must land.
    static let elbowY: CGFloat = 21
    static let gutter: CGFloat = 26
}

// MARK: - Recursive row

private struct NodeView: View {
    let node: TopoNode
    let isLast: Bool
    /// For each ancestor level, whether that ancestor was the last of its
    /// siblings — decides whether a vertical rail continues past this row.
    let ancestorsLast: [Bool]

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .top, spacing: 0) {
                // Continuation rails for ancestors that still have siblings below.
                ForEach(Array(ancestorsLast.dropFirst().enumerated()), id: \.offset) { _, last in
                    railColumn(visible: !last)
                }
                if !ancestorsLast.isEmpty { elbow }
                card
                Spacer(minLength: 0)
            }

            ForEach(Array(node.children.enumerated()), id: \.element.id) { index, child in
                NodeView(node: child,
                         isLast: index == node.children.count - 1,
                         ancestorsLast: ancestorsLast + [isLast])
            }
        }
    }

    // MARK: Connectors

    /// A 1pt vertical line pinned to the left of a fixed-width column. Stretches
    /// to the row's height, which the card determines.
    private func railColumn(visible: Bool) -> some View {
        Rectangle()
            .fill(visible ? TopoStyle.rail : .clear)
            .frame(width: 1)
            .frame(maxHeight: .infinity)
            .frame(width: TopoStyle.gutter, alignment: .leading)
    }

    private var elbow: some View {
        ZStack(alignment: .topLeading) {
            // Vertical: stops at the elbow for a last child, continues otherwise.
            Rectangle()
                .fill(TopoStyle.rail)
                .frame(width: 1)
                .frame(maxHeight: isLast ? TopoStyle.elbowY : .infinity, alignment: .top)
            // Horizontal stub into the card.
            Rectangle()
                .fill(TopoStyle.rail)
                .frame(width: TopoStyle.gutter - 10, height: 1)
                .offset(y: TopoStyle.elbowY)
        }
        .frame(width: TopoStyle.gutter, alignment: .leading)
    }

    // MARK: Card

    private var tint: Color { TopoStyle.tint(node.kind) }

    private var card: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: TopoStyle.symbol(node.kind))
                .font(.system(size: 13))
                .foregroundStyle(tint)
                .frame(width: 26, height: 26)
                .background(tint.opacity(0.15), in: RoundedRectangle(cornerRadius: 7))

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(node.title)
                        .font(.system(size: 12.5, weight: node.kind == .device ? .regular : .semibold))
                    ForEach(node.badges, id: \.self) { badge in
                        Text(badge)
                            .font(.system(size: 9.5, weight: .medium))
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(tint.opacity(0.16), in: Capsule())
                            .foregroundStyle(tint)
                    }
                }
                if let subtitle = node.subtitle {
                    Text(subtitle)
                        .font(.system(size: 9.5, design: .monospaced))
                        .foregroundStyle(.tertiary)
                }
                if let note = node.note {
                    Text(note)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: 430, alignment: .leading)
                }
            }
            .padding(.vertical, 7)
            .padding(.trailing, 12)
        }
        .padding(.leading, 8)
        .background(
            RoundedRectangle(cornerRadius: 9)
                .fill(tint.opacity(node.kind == .device ? 0.05 : 0.09))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 9)
                .strokeBorder(tint.opacity(0.22), lineWidth: 1)
        )
        .fixedSize(horizontal: true, vertical: false)
    }
}
