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
                        .padding(18)
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
        Text("Power flows down this tree, never up. Anything below the Mac is a consumer — "
             + "a monitor's built-in hub looks like infrastructure but supplies the Mac nothing.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
    }
}

/// Recursive row. Draws its own ASCII-style guides so the parent/child
/// relationship stays unambiguous — which is the whole reason for this view.
private struct NodeView: View {
    let node: TopoNode
    let isLast: Bool
    /// For each ancestor level, whether that ancestor was the last of its
    /// siblings — determines whether a vertical guide continues past this row.
    let ancestorsLast: [Bool]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 0) {
                Text(prefix)
                    .font(.system(.body, design: .monospaced))
                    .foregroundStyle(.tertiary)

                Image(systemName: symbol)
                    .foregroundStyle(tint)
                    .frame(width: 20)

                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 6) {
                        Text(node.title)
                            .font(.system(size: 12, weight: weight))
                        ForEach(node.badges, id: \.self) { badge in
                            Text(badge)
                                .font(.system(size: 10))
                                .padding(.horizontal, 5)
                                .padding(.vertical, 1)
                                .background(tint.opacity(0.16), in: Capsule())
                                .foregroundStyle(tint)
                        }
                    }
                    if let subtitle = node.subtitle {
                        Text(subtitle).font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                    }
                    if let note = node.note {
                        Text(note)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: 460, alignment: .leading)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.vertical, 3)

            ForEach(Array(node.children.enumerated()), id: \.element.id) { index, child in
                NodeView(node: child,
                         isLast: index == node.children.count - 1,
                         ancestorsLast: ancestorsLast + [isLast])
            }
        }
    }

    /// Root has no guide; deeper rows draw continuation bars for ancestors that
    /// still have siblings below them.
    private var prefix: String {
        guard !ancestorsLast.isEmpty else { return "" }
        var out = ""
        for last in ancestorsLast.dropFirst() {
            out += last ? "    " : "│   "
        }
        return out + (isLast ? "└── " : "├── ")
    }

    private var symbol: String {
        switch node.kind {
        case .powerSource: return "bolt.fill"
        case .host:        return "laptopcomputer"
        case .thunderbolt: return "cable.connector"
        case .hub:         return "point.3.connected.trianglepath.dotted"
        case .device:      return "circle.fill"
        }
    }

    private var tint: Color {
        switch node.kind {
        case .powerSource: return .yellow
        case .host:        return .blue
        case .thunderbolt: return .purple
        case .hub:         return .orange
        case .device:      return .secondary
        }
    }

    private var weight: Font.Weight {
        switch node.kind {
        case .device: return .regular
        default:      return .semibold
        }
    }
}
