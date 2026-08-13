import SwiftUI

/// The connection diagram: boxes joined by orthogonal connectors, in whichever
/// of the three layouts the user prefers.
struct DiagramView: View {
    let sample: Sample
    /// Absent when viewing a recorded sample, which has nothing to refresh.
    var onRefresh: (() -> Void)?

    @AppStorage("diagramStyle") private var storedStyle = DiagramStyle.cascade.rawValue
    @State private var scale: CGFloat = 1

    private var style: DiagramStyle {
        DiagramStyle(rawValue: storedStyle) ?? .cascade
    }

    private var layout: DiagramLayout {
        Diagram.layout(root: Topology.build(from: sample), style: style)
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            canvas
            Divider()
            legend
        }
    }

    // MARK: - Toolbar

    private var toolbar: some View {
        HStack(spacing: 12) {
            Picker("", selection: $storedStyle) {
                ForEach(DiagramStyle.allCases) { option in
                    Text(option.label).tag(option.rawValue)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 250)
            .help(style.summary)

            Divider().frame(height: 16)

            Button { zoom(-0.1) } label: { Image(systemName: "minus.magnifyingglass") }
                .buttonStyle(.borderless)
            Text("\(Int(scale * 100))%")
                .font(.caption)
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .frame(width: 38)
            Button { zoom(0.1) } label: { Image(systemName: "plus.magnifyingglass") }
                .buttonStyle(.borderless)
            Button("Reset") { scale = 1 }
                .buttonStyle(.borderless)
                .font(.caption)

            Button {
                exportToExcalidraw()
            } label: {
                Label("Export…", systemImage: "square.and.arrow.up")
            }
            .buttonStyle(.borderless)
            .font(.caption)
            .help("Save as an .excalidraw document")

            Spacer()

            // A recording is not stale live data, so it gets a label rather than
            // an age that would climb into the warning colour and imply a fault.
            if let onRefresh {
                FreshnessDot(updated: sample.t)
                Button(action: onRefresh) { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless)
                    .help("Sample now")
            } else {
                Text("recorded \(Diagnosis.stamp(sample.t))")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
    }

    private func zoom(_ delta: CGFloat) {
        scale = min(2.0, max(0.4, scale + delta))
    }

    private func exportToExcalidraw() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "connections.excalidraw"
        panel.allowedContentTypes = []
        panel.message = "Export the connection diagram for Excalidraw"

        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let data = ExcalidrawExport.document(
            layout: layout,
            caption: "TBDoctor — connections as of \(Diagnosis.stamp(sample.t))") else { return }
        try? data.write(to: url)
    }

    // MARK: - Canvas

    private var canvas: some View {
        let placed = layout
        return ScrollView([.horizontal, .vertical]) {
            ZStack(alignment: .topLeading) {
                ForEach(placed.edges) { edge in
                    EdgeShape(points: edge.points)
                        .stroke(edge.kind == .power ? Color.yellow.opacity(0.85) : TopoStyle.rail,
                                style: StrokeStyle(lineWidth: edge.kind == .power ? 2 : 1.4,
                                                   lineJoin: .round))
                }
                ForEach(placed.nodes) { placedNode in
                    NodeBox(node: placedNode.node)
                        .frame(width: placedNode.frame.width, height: placedNode.frame.height)
                        .offset(x: placedNode.frame.minX, y: placedNode.frame.minY)
                }
            }
            .frame(width: placed.size.width, height: placed.size.height, alignment: .topLeading)
            .scaleEffect(scale, anchor: .topLeading)
            // The scaled content still needs a frame at its scaled size, or the
            // ScrollView keeps scrolling the original bounds.
            .frame(width: placed.size.width * scale,
                   height: placed.size.height * scale,
                   alignment: .topLeading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - Legend

    private var legend: some View {
        HStack(spacing: 14) {
            ForEach(TopoStyle.legendItems, id: \.label) { item in
                HStack(spacing: 5) {
                    Circle().fill(item.color).frame(width: 7, height: 7)
                    Text(item.label).font(.caption2).foregroundStyle(.secondary)
                }
            }
            Divider().frame(height: 12)
            HStack(spacing: 5) {
                Rectangle().fill(Color.yellow).frame(width: 14, height: 2)
                Text("power path").font(.caption2).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
    }
}

// MARK: - Pieces

/// Orthogonal polyline through the routed points.
private struct EdgeShape: Shape {
    let points: [CGPoint]

    func path(in rect: CGRect) -> Path {
        var path = Path()
        guard let first = points.first else { return path }
        path.move(to: first)
        for point in points.dropFirst() { path.addLine(to: point) }
        return path
    }
}

private struct NodeBox: View {
    let node: TopoNode

    private var tint: Color { TopoStyle.tint(node.kind) }

    var body: some View {
        HStack(spacing: 9) {
            Image(systemName: TopoStyle.symbol(node.kind))
                .font(.system(size: 12))
                .foregroundStyle(tint)
                .frame(width: 24, height: 24)
                .background(tint.opacity(0.18), in: RoundedRectangle(cornerRadius: 6))

            VStack(alignment: .leading, spacing: 2) {
                Text(node.title)
                    .font(.system(size: 12.5, weight: node.kind == .device ? .regular : .semibold))
                    .lineLimit(1)
                if !node.badges.isEmpty {
                    Text(node.badges.joined(separator: "   "))
                        .font(.system(size: 10.5))
                        .foregroundStyle(tint)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 8)
        .background(
            RoundedRectangle(cornerRadius: 9).fill(tint.opacity(node.kind == .device ? 0.07 : 0.13))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 9).strokeBorder(tint.opacity(0.45), lineWidth: 1.2)
        )
        // The explanatory note does not fit in a box this size, so it lives in
        // the tooltip rather than being dropped.
        .help(helpText)
    }

    private var helpText: String {
        [node.subtitle, node.note].compactMap { $0 }.joined(separator: "\n")
    }
}

/// Shows how old the current sample is, ticking once a second.
///
/// The views refresh on their own each sample tick, but a live view with no
/// evidence of being live reads as stale.
struct FreshnessDot: View {
    let updated: Date
    @State private var now = Date()
    private let tick = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        let age = Int(max(0, now.timeIntervalSince(updated)))
        HStack(spacing: 5) {
            Circle()
                .fill(age <= 8 ? Color.green : Color.orange)
                .frame(width: 6, height: 6)
            Text(age < 2 ? "live" : "\(age)s ago")
                .font(.caption2)
                .foregroundStyle(.secondary)
                .monospacedDigit()
        }
        .onReceive(tick) { now = $0 }
    }
}
