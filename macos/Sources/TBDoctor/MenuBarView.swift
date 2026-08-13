import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject var collector: Collector

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header
            if collector.storeConflict {
                Text("Another TBDoctor is already collecting — this one is read-only. Quit the other copy.")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Divider()
            statusRows
            if let finding = collector.findings.first {
                Divider()
                rootCause(finding)
            }
            if let incident = collector.lastIncident {
                Divider()
                lastIncident(incident)
            }
            Divider()
            footer
        }
        .padding(14)
        .frame(width: 340)
    }

    // MARK: - Sections

    private var header: some View {
        HStack {
            Image(systemName: Health.symbol(for: collector.health))
                .foregroundStyle(Health.tint(for: collector.health))
            Text(Health.title(for: collector.health))
                .font(.headline)
            Spacer()
        }
    }

    @ViewBuilder
    private var statusRows: some View {
        if let sample = collector.current {
            VStack(alignment: .leading, spacing: 6) {
                if let device = sample.tb.first {
                    row("Thunderbolt", device.label,
                        detail: device.linkGbps.map { String(format: "%.0f Gb/s", $0) })
                } else {
                    row("Thunderbolt", "no device", detail: nil, muted: true)
                }

                row("Adapter", sample.adapter.summary,
                    detail: sample.adapter.id.map { "ID \($0)" })

                row("Battery", "\(sample.percent)%",
                    detail: String(format: "%d mA · %.1f W", sample.amperageMilliAmps, sample.batteryWatts))

                row("USB devices", "\(sample.usb.count)",
                    detail: sample.externalConnected ? "on AC" : "on battery")
            }
        } else {
            Text("Starting up…").foregroundStyle(.secondary)
        }
    }

    private func rootCause(_ finding: Finding) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Root cause").font(.caption).foregroundStyle(.secondary)
            Text(finding.title).font(.system(size: 12, weight: .semibold))
            Text(finding.explanation)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text("confidence: \(finding.confidence)")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }

    private func lastIncident(_ incident: Incident) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text("Last incident").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Text(Diagnosis.stamp(incident.start)).font(.caption).foregroundStyle(.secondary)
            }
            Text("↳ \(incident.headline)").font(.caption)
            if !incident.devicesLost.isEmpty {
                Text("lost: \(incident.devicesLost.prefix(3).joined(separator: ", "))")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
        }
    }

    private var footer: some View {
        HStack {
            Button("Open timeline…") { TimelineWindow.shared.show() }
            Spacer()
            Button("Quit") { NSApplication.shared.terminate(nil) }
                .foregroundStyle(.secondary)
        }
        .buttonStyle(.plain)
        .font(.caption)
    }

    // MARK: - Row helper

    private func row(_ label: String, _ value: String, detail: String?, muted: Bool = false) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(label)
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(width: 92, alignment: .leading)
            VStack(alignment: .leading, spacing: 1) {
                Text(value)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(muted ? .secondary : .primary)
                if let detail {
                    Text(detail).font(.caption2).foregroundStyle(.secondary)
                }
            }
            Spacer()
        }
    }
}

/// Menu bar state is encoded in the *symbol* as well as the colour, so it stays
/// legible in a monochrome menu bar and for colourblind users.
enum Health {
    static func symbol(for severity: Severity) -> String {
        switch severity {
        case .info: return "bolt.horizontal.circle"
        case .warning: return "exclamationmark.triangle.fill"
        case .critical: return "xmark.octagon.fill"
        }
    }

    static func tint(for severity: Severity) -> Color {
        switch severity {
        case .info: return .green
        case .warning: return .orange
        case .critical: return .red
        }
    }

    static func title(for severity: Severity) -> String {
        switch severity {
        case .info: return "Nominal"
        case .warning: return "Degraded"
        case .critical: return "Fault detected"
        }
    }
}
