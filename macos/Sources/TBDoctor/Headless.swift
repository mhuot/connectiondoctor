import Foundation

/// Terminal front end. Exists for three reasons: it makes the diagnosis engine
/// testable without driving a GUI, it works over SSH, and it produces something
/// you can paste into a support ticket.
enum Headless {

    static func run(_ arguments: [String]) -> Bool {
        if arguments.contains("--mcp") { MCPServer.serve(); return true }
        if arguments.contains("--probe") { probe(); return true }
        if arguments.contains("--report") { report(); return true }
        if arguments.contains("--watch") { watch(); return true }
        if arguments.contains("--help") || arguments.contains("-h") { usage(); return true }
        return false
    }

    private static func usage() {
        print("""
        TBDoctor — Thunderbolt, USB and power fault diagnosis

          (no flags)   launch the menu bar app
          --probe      print current state once and exit
          --report     analyse recorded history and print findings
          --watch      stream a live one-line-per-sample table
          --mcp        run as an MCP server on stdio (for coding agents)
          --help       this message

        Recorded data lives in \(Store.directory.path)

        To register with Claude Code:
          claude mcp add tbdoctor -- \(CommandLine.arguments.first ?? "tbdoctor") --mcp
        """)
    }

    // MARK: - Point-in-time state

    static func probe() {
        let sample = Probes.sample()

        print("Thunderbolt")
        if sample.tb.isEmpty {
            print("  no device connected")
        } else {
            for device in sample.tb {
                let speed = device.linkGbps.map { String(format: "%.0f Gb/s", $0) } ?? "unknown"
                print("  \(device.label) — \(speed), depth \(device.depth), route \(device.route)")
            }
        }

        print("\nPower")
        print("  adapter        \(sample.adapter.summary)")
        if let id = sample.adapter.id { print("  adapter ID     \(id)") }
        if let serial = sample.adapter.serial { print("  serial         \(serial)") }
        print("  external       \(sample.externalConnected ? "yes" : "no")")
        print(String(format: "  battery        %d%%  %d mA  %.1f W",
                     sample.percent, sample.amperageMilliAmps, sample.batteryWatts))

        print("\nUSB (\(sample.usb.count) devices)")
        for device in sample.usb {
            print(String(format: "  0x%08X  %-44@ %@",
                         device.locationID, device.name as NSString, device.speedLabel))
        }
    }

    // MARK: - Retrospective analysis

    static func report() {
        let sampleStore = Store(filename: "samples.jsonl")
        let eventStore = Store(filename: "events.jsonl")
        var samples = sampleStore.load(Sample.self) { $0.t }
        let events = eventStore.load(KernelEvent.self) { $0.t }

        // Always fold in a live reading so `--report` says something useful even
        // on a machine that has never run the collector.
        samples.append(Probes.sample())

        print("TBDoctor report")
        print("  \(samples.count) samples, \(events.count) kernel events on record")
        if let first = samples.first, let last = samples.last {
            print("  window: \(Diagnosis.stamp(first.t)) → \(Diagnosis.stamp(last.t))")
        }

        let incidents = Collector.deriveIncidents(samples: samples, events: events)
        print("\nIncidents: \(incidents.count)")
        for incident in incidents.prefix(10) {
            print("  \(Diagnosis.stamp(incident.start))  \(incident.headline)")
            if let peak = incident.peakDischargeMilliAmps, peak < 0 {
                print("      peak discharge \(peak) mA")
            }
            if !incident.devicesLost.isEmpty {
                print("      lost \(incident.devicesLost.count): \(incident.devicesLost.prefix(5).joined(separator: ", "))")
            }
        }

        let findings = Diagnosis.analyze(samples: samples, events: events)
        print("\nFindings: \(findings.count)")
        if findings.isEmpty { print("  nothing anomalous on record") }

        for finding in findings {
            let marker: String
            switch finding.severity {
            case .critical: marker = "!!"
            case .warning:  marker = "! "
            case .info:     marker = "  "
            }
            print("\n\(marker) \(finding.title)  [confidence: \(finding.confidence)]")
            print(wrap(finding.explanation, indent: "     "))
            for line in finding.evidence { print("     • \(line)") }
            if let recommendation = finding.recommendation {
                print("     → " + wrap(recommendation, indent: "       ").trimmingCharacters(in: .whitespaces))
            }
        }
    }

    // MARK: - Live table

    static func watch() {
        print("time      TB          adapter      mA      W     USB")
        while true {
            let s = Probes.sample()
            let tb = s.tb.first.map { $0.model } ?? "—"
            let adapter = s.adapter.watts.map { "\($0)W/\(s.adapter.id ?? 0)" } ?? "none"
            print(String(format: "%@  %-10@  %-11@  %-6d  %-5.1f %d",
                         time(s.t), tb as NSString, adapter as NSString,
                         s.amperageMilliAmps, s.batteryWatts, s.usb.count))
            fflush(stdout)
            Thread.sleep(forTimeInterval: 2)
        }
    }

    // MARK: - Helpers

    private static func time(_ date: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f.string(from: date)
    }

    private static func wrap(_ text: String, indent: String, width: Int = 76) -> String {
        var lines: [String] = []
        var line = indent
        for word in text.split(separator: " ") {
            if line.count + word.count + 1 > width {
                lines.append(line)
                line = indent
            }
            line += (line == indent ? "" : " ") + word
        }
        if line != indent { lines.append(line) }
        return lines.joined(separator: "\n")
    }
}
