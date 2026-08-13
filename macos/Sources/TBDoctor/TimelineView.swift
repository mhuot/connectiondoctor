import SwiftUI
import Charts

struct TimelineView: View {
    @EnvironmentObject var collector: Collector

    private enum Pane: String { case timeline, connections }
    @State private var pane: Pane = .timeline
    @State private var window: TimeInterval = 3600

    private var windowed: [Sample] {
        let cutoff = Date().addingTimeInterval(-window)
        return collector.samples.filter { $0.t >= cutoff }
    }

    private var windowedEvents: [KernelEvent] {
        let cutoff = Date().addingTimeInterval(-window)
        // Only root events get plotted. Marking all of them would paint a solid
        // wall during a burst and hide the one line that identifies the cause.
        return collector.events.filter { $0.t >= cutoff && $0.kind.isRoot }
    }

    var body: some View {
        HSplitView {
            leftPane.frame(minWidth: 540, maxHeight: .infinity)
            findings.frame(minWidth: 320, maxWidth: 480, maxHeight: .infinity)
        }
        .frame(minWidth: 940, minHeight: 560)
    }

    // MARK: - Left pane

    private var leftPane: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Picker("", selection: $pane) {
                    Text("Timeline").tag(Pane.timeline)
                    Text("Connections").tag(Pane.connections)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 210)

                Spacer()

                if pane == .timeline {
                    Picker("", selection: $window) {
                        Text("15m").tag(TimeInterval(900))
                        Text("1h").tag(TimeInterval(3600))
                        Text("6h").tag(TimeInterval(21600))
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 170)
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 12)

            switch pane {
            case .timeline:    charts
            case .connections: connections
            }
        }
        // Top-aligned and free to fill: without this the content floated in the
        // middle of the pane and left a large dead band under the title bar.
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    // MARK: - Connections

    @ViewBuilder
    private var connections: some View {
        if let sample = collector.current {
            TopologyView(sample: sample)
        } else {
            ContentUnavailableView("Nothing enumerated yet", systemImage: "cable.connector")
        }
    }

    // MARK: - Charts

    @ViewBuilder
    private var charts: some View {
        if windowed.isEmpty {
            ContentUnavailableView("No samples yet", systemImage: "clock",
                                   description: Text("Data appears within a few seconds of launch."))
        } else {
            VStack(alignment: .leading, spacing: 12) {
                // Step interpolation, not linear: the link is up or down, and a
                // sloped line between the two would imply states that never existed.
                chart("Thunderbolt link") {
                    ForEach(windowed, id: \.t) { sample in
                        AreaMark(x: .value("Time", sample.t),
                                 y: .value("Link", sample.tbConnected ? 1 : 0))
                        .interpolationMethod(.stepEnd)
                        .foregroundStyle(.green.opacity(0.30))
                    }
                    eventMarks
                }
                .chartYScale(domain: 0...1)
                .chartYAxis(.hidden)

                chart("Power (W) — blue: adapter rating · orange: battery") {
                    ForEach(windowed, id: \.t) { sample in
                        LineMark(x: .value("Time", sample.t),
                                 y: .value("Adapter", Double(sample.adapter.watts ?? 0)),
                                 series: .value("s", "adapter"))
                        .foregroundStyle(.blue)
                        // Negative while discharging, so anything below the axis
                        // is demand the adapter did not cover.
                        LineMark(x: .value("Time", sample.t),
                                 y: .value("Battery", sample.batteryWatts),
                                 series: .value("s", "battery"))
                        .foregroundStyle(.orange)
                    }
                    RuleMark(y: .value("Zero", 0)).foregroundStyle(.secondary.opacity(0.3))
                    eventMarks
                }

                chart("USB devices") {
                    ForEach(windowed, id: \.t) { sample in
                        LineMark(x: .value("Time", sample.t),
                                 y: .value("Devices", sample.usb.count))
                        .interpolationMethod(.stepEnd)
                        .foregroundStyle(.purple)
                    }
                    eventMarks
                }
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 16)
        }
    }

    @ChartContentBuilder
    private var eventMarks: some ChartContent {
        ForEach(windowedEvents) { event in
            RuleMark(x: .value("Event", event.t))
                .foregroundStyle(.red.opacity(0.65))
                .lineStyle(StrokeStyle(lineWidth: 1, dash: [3, 2]))
        }
    }

    /// Charts flex to share whatever height the window has, rather than being
    /// pinned to a fixed height that leaves the pane half empty.
    private func chart<C: ChartContent>(_ title: String,
                                        @ChartContentBuilder content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Chart(content: content).frame(minHeight: 80, maxHeight: .infinity)
        }
        .frame(maxHeight: .infinity)
    }

    // MARK: - Findings panel

    private var findings: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Diagnosis").font(.headline)

                if collector.findings.isEmpty {
                    Text("Nothing anomalous in the current window.")
                        .font(.caption).foregroundStyle(.secondary)
                }

                ForEach(collector.findings) { finding in
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 6) {
                            Image(systemName: Health.symbol(for: finding.severity))
                                .foregroundStyle(Health.tint(for: finding.severity))
                            Text(finding.title).font(.system(size: 12, weight: .semibold))
                        }
                        Text(finding.explanation)
                            .font(.caption)
                            .fixedSize(horizontal: false, vertical: true)

                        // Evidence is shown inline, not hidden behind a
                        // disclosure — a verdict you cannot check is useless.
                        ForEach(finding.evidence, id: \.self) { line in
                            Text("• \(line)")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }

                        if let recommendation = finding.recommendation {
                            Text(recommendation)
                                .font(.caption)
                                .padding(8)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(Color.accentColor.opacity(0.10), in: RoundedRectangle(cornerRadius: 6))
                                .fixedSize(horizontal: false, vertical: true)
                        }

                        Text("confidence: \(finding.confidence)")
                            .font(.caption2).foregroundStyle(.tertiary)
                    }
                    Divider()
                }

                incidentList
            }
            .padding(16)
            .frame(maxHeight: .infinity, alignment: .top)
        }
    }

    private var incidentList: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Incidents").font(.headline)
            if collector.incidents.isEmpty {
                Text("None recorded.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(collector.incidents.prefix(12)) { incident in
                VStack(alignment: .leading, spacing: 2) {
                    Text(Diagnosis.stamp(incident.start)).font(.caption).bold()
                    Text(incident.headline).font(.caption2).foregroundStyle(.secondary)
                    if let peak = incident.peakDischargeMilliAmps, peak < 0 {
                        Text("peak discharge \(peak) mA").font(.caption2).foregroundStyle(.tertiary)
                    }
                    if !incident.devicesLost.isEmpty {
                        Text("lost \(incident.devicesLost.count): \(incident.devicesLost.prefix(4).joined(separator: ", "))")
                            .font(.caption2).foregroundStyle(.tertiary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
    }
}
