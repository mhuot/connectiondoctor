import Foundation

/// Severity drives both ordering and the menu bar dot colour.
enum Severity: Int, Codable, Comparable {
    case info = 0, warning = 1, critical = 2
    static func < (a: Severity, b: Severity) -> Bool { a.rawValue < b.rawValue }
}

/// A root-cause conclusion with the evidence that produced it. The evidence
/// strings matter as much as the verdict — a diagnosis you can't audit is just
/// an opinion, and the failure modes here are easy to get confidently wrong.
struct Finding: Identifiable, Codable {
    var id = UUID()
    var title: String
    var severity: Severity
    var confidence: String
    var explanation: String
    var evidence: [String]
    var recommendation: String?
}

/// The rules engine. Each `check` is an independent hypothesis tested against
/// the sample and event streams; they are deliberately allowed to fire together
/// so a fault with two contributing causes reports both.
enum Diagnosis {

    /// Below this, negative current is just charge-maintenance noise. A machine
    /// sitting at 100% trickles a few hundred milliwatts in and out constantly
    /// and calling that a "deficit" would make the tool cry wolf permanently.
    static let deficitWattsThreshold = 10.0
    /// A deficit has to persist to count — a single sample can catch a transient
    /// spike that the adapter absorbs perfectly well.
    static let deficitMinSamples = 2
    /// How many non-deficit samples may sit inside a deficit run without ending
    /// it. This is not cosmetic: during a real fault the adapter drops out and
    /// returns repeatedly, so requiring an unbroken run discards precisely the
    /// samples that matter and the fault reads as healthy.
    static let deficitGapTolerance = 3
    /// How close a link drop has to sit to a deficit before we call them related.
    static let correlationWindow: TimeInterval = 20

    // MARK: - Entry point

    static func analyze(samples: [Sample], events: [KernelEvent]) -> [Finding] {
        var findings: [Finding] = []
        findings += powerDeficit(samples: samples, events: events)
        findings += adapterArbitration(samples: samples)
        findings += linkIntegrity(samples: samples, events: events)
        findings += deviceLoss(samples: samples)
        findings += headroom(samples: samples)
        findings += linkSpeed(samples: samples)
        return findings.sorted { a, b in
            a.severity == b.severity ? a.title < b.title : a.severity > b.severity
        }
    }

    // MARK: - Deficit periods

    struct DeficitPeriod {
        var start: Date
        var end: Date
        var peakWatts: Double
        var adapter: AdapterInfo
        var everLostAC: Bool
    }

    /// Contiguous stretches where the battery was supplying real power *while
    /// the machine believed it was on AC*. That contradiction is the signature
    /// of an under-served supply.
    static func deficitPeriods(_ samples: [Sample]) -> [DeficitPeriod] {
        var periods: [DeficitPeriod] = []
        var run: [Sample] = []
        var deficitCount = 0
        var gap = 0

        func isDeficit(_ sample: Sample) -> Bool {
            sample.amperageMilliAmps < 0 && abs(sample.batteryWatts) >= deficitWattsThreshold
        }

        func flush() {
            defer { run = []; deficitCount = 0; gap = 0 }
            guard deficitCount >= deficitMinSamples, let first = run.first, let last = run.last else { return }

            // Qualify the period as a whole rather than per-sample: the machine
            // must have believed it was plugged in at some point during it.
            // Otherwise this is a laptop deliberately running on battery, which
            // is not a fault.
            guard run.contains(where: { $0.adapter.isPresent || $0.externalConnected }) else { return }

            periods.append(DeficitPeriod(
                start: first.t,
                end: last.t,
                peakWatts: run.map { abs($0.batteryWatts) }.max() ?? 0,
                // Prefer a sample where the adapter actually identified itself,
                // so the period is attributed to the real supply rather than to
                // one of its dropout moments.
                adapter: run.first(where: { $0.adapter.isPresent })?.adapter ?? first.adapter,
                everLostAC: run.contains { !$0.externalConnected }))
        }

        for sample in samples {
            if isDeficit(sample) {
                run.append(sample)
                deficitCount += 1
                gap = 0
            } else if !run.isEmpty {
                gap += 1
                if gap > deficitGapTolerance { flush() } else { run.append(sample) }
            }
        }
        flush()
        return periods
    }

    private static func powerDeficit(samples: [Sample], events: [KernelEvent]) -> [Finding] {
        let periods = deficitPeriods(samples)
        guard !periods.isEmpty else { return [] }

        let worst = periods.max { $0.peakWatts < $1.peakWatts }!
        let supplied = Double(worst.adapter.watts ?? 0)
        let demand = supplied + worst.peakWatts

        var evidence: [String] = []
        // Lead with the directly measured quantity. Total demand is inferred by
        // adding the adapter's *rating* to the battery's contribution, and an
        // adapter under stress may not be delivering its full rating — so that
        // figure is an upper bound, and is labelled as one.
        evidence.append("Battery supplied up to \(fmt(worst.peakWatts))W while the machine reported AC power")
        evidence.append("\(periods.count) deficit period(s); worst ran \(stamp(worst.start))–\(stamp(worst.end))")
        evidence.append("Adapter rated \(Int(supplied))W → demand ≈ \(Int(demand.rounded()))W (approx: rating + battery contribution)")
        if worst.everLostAC {
            evidence.append("ExternalConnected dropped to No mid-period — the machine briefly ran entirely on battery")
        }

        // The decisive correlation: did the Thunderbolt link fail while the
        // supply was underwater? If so the power problem is not incidental.
        let drops = events.filter { $0.kind.isRoot }
        let correlated = drops.filter { drop in
            periods.contains { drop.t >= $0.start.addingTimeInterval(-correlationWindow)
                            && drop.t <= $0.end.addingTimeInterval(correlationWindow) }
        }

        var severity = Severity.warning
        var confidence = "moderate"
        var explanation = """
        The power adapter could not cover demand and the battery made up the difference \
        while still reporting AC power.
        """
        var recommendation = "Use a higher-wattage supply, or reduce what the dock is being asked to carry."

        if !correlated.isEmpty {
            severity = .critical
            confidence = "high"
            evidence.append("\(correlated.count) Thunderbolt link drop(s) fell inside the deficit window — e.g. \(stamp(correlated[0].t))")
            explanation = """
            The supply ran short and the Thunderbolt link dropped while it was short. When demand \
            exceeds what the supply can deliver, USB-C power delivery renegotiates; that renegotiation \
            resets the port, and a port reset tears down the Thunderbolt link and everything behind it.
            """
        }

        if worst.adapter.looksLikeDock {
            confidence = correlated.isEmpty ? "high" : "very high"
            evidence.append("The supply at the time was the dock itself (no adapter ID, no manufacturer string)")
            explanation += """
             The supply was the dock, so the same cable was carrying both power and data — \
            which is why a power shortfall was able to take out the data link.
            """
            recommendation = """
            Put a properly rated charger directly on the laptop and let the dock handle data only. \
            Separating power from the data link means a shortfall can no longer reset the port. \
            Alternatively move to a dock whose host charging exceeds the machine's peak demand \
            (≈\(Int(demand.rounded()))W observed here).
            """
        }

        return [Finding(
            title: "Power supply under-served",
            severity: severity,
            confidence: confidence,
            explanation: explanation,
            evidence: evidence,
            recommendation: recommendation)]
    }

    // MARK: - Two supplies fighting

    private static func adapterArbitration(samples: [Sample]) -> [Finding] {
        let ids = samples.compactMap { $0.adapter.isPresent ? ($0.t, $0.adapter.id ?? 0) : nil }
        guard ids.count > 2 else { return [] }

        var flips: [(Date, Int, Int)] = []
        for i in 1..<ids.count where ids[i].1 != ids[i - 1].1 {
            flips.append((ids[i].0, ids[i - 1].1, ids[i].1))
        }
        guard flips.count >= 3 else { return [] }

        return [Finding(
            title: "Power source switching repeatedly",
            severity: .warning,
            confidence: "moderate",
            explanation: """
            The active power adapter changed identity several times. With two supplies attached, \
            macOS arbitrates between them, and each handover renegotiates power delivery — which can \
            reset the port that renegotiation happens on.
            """,
            evidence: flips.prefix(4).map { "\(stamp($0.0)): adapter ID \($0.1) → \($0.2)" },
            recommendation: "Settle on one supply, or make sure the second one is not on the port carrying Thunderbolt.")]
    }

    // MARK: - Link integrity (the not-power explanation)

    private static func linkIntegrity(samples: [Sample], events: [KernelEvent]) -> [Finding] {
        let drops = events.filter { $0.kind.isRoot }
        guard !drops.isEmpty else { return [] }

        let periods = deficitPeriods(samples)
        let uncorrelated = drops.filter { drop in
            !periods.contains { drop.t >= $0.start.addingTimeInterval(-correlationWindow)
                             && drop.t <= $0.end.addingTimeInterval(correlationWindow) }
        }
        guard uncorrelated.count >= 2 else { return [] }

        // Rapid up/down cycling with a short, regular dwell points at link
        // training failing rather than anything physical being disturbed — a
        // wiggled connector produces irregular timing, not a metronome.
        var dwells: [TimeInterval] = []
        for i in 1..<uncorrelated.count {
            let gap = uncorrelated[i].t.timeIntervalSince(uncorrelated[i - 1].t)
            if gap < 10 { dwells.append(gap) }
        }
        let regular = dwells.count >= 2 && (dwells.max()! - dwells.min()!) < 1.0

        var evidence = ["\(uncorrelated.count) link drop(s) with no power deficit nearby"]
        evidence.append("First at \(stamp(uncorrelated[0].t))")
        if regular, let mean = dwells.first {
            evidence.append(String(format: "Link held ~%.1fs between drops, consistently — link training failing, not a disturbed connector", mean))
        }

        return [Finding(
            title: "Thunderbolt link dropping without a power deficit",
            severity: .critical,
            confidence: regular ? "high" : "moderate",
            explanation: """
            The link failed at times when the supply was comfortably covering demand, which points at \
            the physical link rather than power — cable, connector, or port.
            """,
            evidence: evidence,
            recommendation: """
            Try a different certified Thunderbolt cable and a different port on the Mac. Passive USB-C \
            cables longer than 0.8m cannot carry Thunderbolt at full rate at all.
            """)]
    }

    // MARK: - Device loss attribution

    /// USB location IDs are hierarchical: 0x01134300 sits under 0x01134000,
    /// which sits under 0x01130000. Sharing a prefix means sharing a hub, so a
    /// batch of devices that vanish together can be blamed on their common
    /// parent instead of being reported as N unrelated disappearances.
    static func commonAncestor(_ locations: [UInt32]) -> UInt32? {
        guard let first = locations.first else { return nil }
        guard locations.count > 1 else { return first & 0xFFFF_0000 }
        var mask: UInt32 = 0
        for shift in stride(from: 28, through: 0, by: -4) {
            let nibbleMask = mask | (0xF << UInt32(shift))
            let prefix = first & nibbleMask
            if locations.allSatisfy({ $0 & nibbleMask == prefix }) { mask = nibbleMask } else { break }
        }
        return mask == 0 ? nil : (first & mask)
    }

    private static func deviceLoss(samples: [Sample]) -> [Finding] {
        guard samples.count > 1 else { return [] }
        var worst: (t: Date, lost: [USBDevice])? = nil

        for i in 1..<samples.count {
            let before = Set(samples[i - 1].usb.map { $0.locationID })
            let after = Set(samples[i].usb.map { $0.locationID })
            let goneIDs = before.subtracting(after)
            guard goneIDs.count >= 2 else { continue }
            let gone = samples[i - 1].usb.filter { goneIDs.contains($0.locationID) }
            if gone.count > (worst?.lost.count ?? 0) { worst = (samples[i].t, gone) }
        }

        guard let event = worst else { return [] }
        var evidence = ["\(event.lost.count) devices disappeared at \(stamp(event.t))"]
        evidence.append("Lost: " + event.lost.prefix(6).map(\.name).joined(separator: ", "))

        var explanation = "Several USB devices vanished simultaneously."
        if let ancestor = commonAncestor(event.lost.map { $0.locationID }) {
            evidence.append(String(format: "All share location prefix 0x%08X — they sit behind one common hub", ancestor))
            explanation = """
            Several USB devices vanished at the same instant and they all sit behind a single hub. \
            That is one upstream failure, not several device failures — the individual devices are \
            almost certainly innocent.
            """
        }

        return [Finding(
            title: "Devices lost as a group",
            severity: .warning,
            confidence: "high",
            explanation: explanation,
            evidence: evidence,
            recommendation: "Investigate the shared parent, not the individual devices.")]
    }

    // MARK: - Ongoing headroom (fires without any incident at all)

    /// Early warning for a supply that is *nearly* short. Deliberately keyed on
    /// the battery's measured contribution alone: IOKit exposes the adapter's
    /// rating, never what it is actually delivering, so the rating says nothing
    /// about consumption and using it as a proxy makes this fire constantly.
    /// Anything at or past `deficitWattsThreshold` belongs to the deficit
    /// finding instead, so this covers only the band below it.
    private static func headroom(samples: [Sample]) -> [Finding] {
        guard let latest = samples.last, let watts = latest.adapter.watts, watts > 0 else { return [] }

        let contribution = samples.map { max(0, -$0.batteryWatts) }.max() ?? 0
        guard contribution >= 2, contribution < deficitWattsThreshold else { return [] }

        return [Finding(
            title: "Little power headroom left",
            severity: .info,
            confidence: "moderate",
            explanation: """
            The battery has been topping up the adapter by a small amount. Nothing has failed, but the \
            supply is close to its limit — which is the state that turns into a fault under load, \
            during a call or a build rather than while the machine sits still.
            """,
            evidence: [
                "Adapter rated \(watts)W",
                String(format: "Battery contributed up to %.1fW at peak (measured)", contribution),
                String(format: "Still %.1fW below the %.0fW deficit threshold", deficitWattsThreshold - contribution, deficitWattsThreshold)
            ],
            recommendation: nil)]
    }

    // MARK: - Negotiated speed

    private static func linkSpeed(samples: [Sample]) -> [Finding] {
        guard let latest = samples.last, let device = latest.tb.first, let link = device.linkGbps else { return [] }
        // A dock that has fallen back to USB-C would not appear as a Thunderbolt
        // switch at all, so this only catches a genuine speed downgrade.
        guard link < 40 else { return [] }
        return [Finding(
            title: "Thunderbolt link below 40 Gb/s",
            severity: .warning,
            confidence: "moderate",
            explanation: "\(device.label) negotiated \(fmt(link)) Gb/s, below the Thunderbolt 3/4 floor.",
            evidence: ["Negotiated \(fmt(link)) Gb/s at \(stamp(latest.t))"],
            recommendation: "Check the cable — this usually means a cable that cannot carry the full rate.")]
    }

    // MARK: - Formatting

    private static func fmt(_ value: Double) -> String { String(format: "%.1f", value) }

    private static let stampFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "MMM d HH:mm:ss"
        return f
    }()

    static func stamp(_ date: Date) -> String { stampFormatter.string(from: date) }
}
