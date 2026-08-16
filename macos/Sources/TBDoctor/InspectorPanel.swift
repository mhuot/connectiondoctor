import SwiftUI
import AppKit

/// Everything known about one node. Exists because the diagram's boxes are
/// deliberately small, and identifying an anonymous device — a hub whose only
/// name is "USB2.0 Hub" — needs the vendor and product IDs, not a prettier label.
struct InspectorPanel: View {
    let node: TopoNode
    var onClose: () -> Void

    @State private var copied: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if let note = node.note {
                        Text(note.replacingOccurrences(of: "*", with: ""))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    if node.details.isEmpty {
                        Text("No further detail published for this node.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        VStack(alignment: .leading, spacing: 9) {
                            ForEach(node.details) { detail in
                                row(detail)
                            }
                        }
                    }

                    if node.vidPid != nil { research }
                }
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(width: 320)
        .background(.ultraThinMaterial)
    }

    // MARK: - Header

    private var header: some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: TopoStyle.symbol(node.kind))
                .font(.system(size: 12))
                .foregroundStyle(TopoStyle.tint(node.kind))
                .frame(width: 24, height: 24)
                .background(TopoStyle.tint(node.kind).opacity(0.18), in: RoundedRectangle(cornerRadius: 6))

            VStack(alignment: .leading, spacing: 2) {
                Text(node.title)
                    .font(.system(size: 13, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                if !node.badges.isEmpty {
                    Text(node.badges.joined(separator: "  ·  "))
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer(minLength: 0)

            Button(action: onClose) { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless)
                .foregroundStyle(.tertiary)
        }
        .padding(14)
    }

    // MARK: - Rows

    private func row(_ detail: NodeDetail) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(detail.label)
                .font(.caption2)
                .foregroundStyle(.secondary)
            HStack(spacing: 6) {
                Text(detail.value)
                    .font(.system(size: 11.5, design: .monospaced))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                Button {
                    copy(detail.value, label: detail.label)
                } label: {
                    Image(systemName: copied == detail.label ? "checkmark" : "doc.on.doc")
                        .font(.system(size: 9))
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.tertiary)
                .help("Copy")
            }
        }
    }

    // MARK: - Research

    private var research: some View {
        VStack(alignment: .leading, spacing: 8) {
            Divider()
            Text("Identify this device")
                .font(.caption)
                .foregroundStyle(.secondary)
            Text("USB vendor and product IDs are assigned by USB-IF and are the same on every OS, so they identify hardware that names itself uselessly.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)

            if let vidPid = node.vidPid {
                Button {
                    lookUp(vidPid)
                } label: {
                    Label("Look up \(vidPid) online", systemImage: "magnifyingglass")
                        .font(.caption)
                }
                .buttonStyle(.borderless)

                Button {
                    copy(vidPid, label: "vidpid")
                } label: {
                    Label(copied == "vidpid" ? "Copied" : "Copy VID:PID", systemImage: "doc.on.doc")
                        .font(.caption)
                }
                .buttonStyle(.borderless)
            }
        }
    }

    // MARK: - Actions

    private func copy(_ value: String, label: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(value, forType: .string)
        copied = label
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            if copied == label { copied = nil }
        }
    }

    /// Opens a public USB ID database. Only the vendor and product IDs leave the
    /// machine — they are public identifiers shared by every device of that
    /// model, not anything specific to this Mac.
    private func lookUp(_ vidPid: String) {
        let parts = vidPid.split(separator: ":")
        guard parts.count == 2,
              let url = URL(string: "https://devicehunt.com/view/type/usb/vendor/\(parts[0])/device/\(parts[1])")
        else { return }
        NSWorkspace.shared.open(url)
    }
}
