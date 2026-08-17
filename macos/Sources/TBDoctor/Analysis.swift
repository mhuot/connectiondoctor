import Foundation

/// The contract's `findings[]`, `incidents[]` and `analysis{}` for the
/// envelope, computed over the recorder's on-disk history (Contract v1
/// § Envelope, change `contract-findings-incidents`).
///
/// One rule above all: **absent ≠ empty**. When the recorder has never run
/// there is nothing to say, and the envelope carries no `analysis` at all — a
/// consumer can then tell "nothing found" from "nothing recorded". When there
/// is history, `analysis.coverage` states exactly what the recording can vouch
/// for, so an empty stream, a recorder that started ten minutes ago and a
/// trimmed log stop looking alike.
enum Analysis {

    /// Sample cadence the recorder promises; a gap longer than three of these
    /// is a hole in the evidence, not sampling jitter.
    static let sampleInterval: TimeInterval = 5
    static let gapTolerance: TimeInterval = sampleInterval * 3
    static let defaultWindowHours: Double = 6

    struct Result {
        var findings: [Finding]
        var incidents: [Incident]
        var windowHours: Double
        var generatedAt: Date
        var availableFrom: Date
        var through: Date
        var complete: Bool
        var reasons: [String]
    }

    /// Runs the engines over the recorded history inside the window. Returns
    /// nil when there is no history at all (the envelope then omits analysis).
    static func run(windowHours: Double = defaultWindowHours,
                    now: Date = Date(),
                    liveSample: Sample? = nil) -> Result? {
        let windowStart = now.addingTimeInterval(-windowHours * 3600)
        let sampleStore = Store(filename: "samples.jsonl")
        var samples = sampleStore.load(Sample.self, since: windowStart) { $0.t }
        let events = Store(filename: "events.jsonl").load(KernelEvent.self, since: windowStart) { $0.t }

        if samples.isEmpty && events.isEmpty {
            // Nothing inside the window. Two very different situations: the
            // recorder has never run here (no analysis at all — absent ≠ empty),
            // or it ran and stopped before the window (say so, with the time of
            // the last evidence, so a consumer shows "unknown", not "healthy").
            guard let lastRecorded = sampleStore.load(Sample.self) { $0.t }.last?.t else { return nil }
            return Result(findings: [], incidents: [], windowHours: windowHours, generatedAt: now,
                          availableFrom: lastRecorded, through: lastRecorded,
                          complete: false, reasons: ["recorder-stopped-before-window"])
        }

        // Coverage is judged on the recording alone. A live probe folded in
        // for the engines must not make a cold machine look continuously
        // recorded, so measure first, then append.
        let recorded = samples
        if let live = liveSample { samples.append(live) }

        let findings = Diagnosis.analyze(samples: samples, events: events)
        let incidents = Collector.deriveIncidents(samples: samples, events: events)

        var reasons: [String] = []
        let availableFrom = recorded.first?.t ?? events.first?.t ?? now
        let through = recorded.last?.t ?? events.last?.t ?? now
        if availableFrom > windowStart.addingTimeInterval(gapTolerance) {
            reasons.append("recorder-started-inside-window")
        }
        if zip(recorded, recorded.dropFirst()).contains(where: { $1.t.timeIntervalSince($0.t) > gapTolerance }) {
            reasons.append("gap")
        }
        if now.timeIntervalSince(through) > gapTolerance {
            // The recorder stopped before now: the tail of the window is unobserved.
            reasons.append("gap")
        }
        if recorded.isEmpty { reasons.append("no-history") }

        return Result(findings: findings,
                      incidents: incidents,
                      windowHours: windowHours,
                      generatedAt: now,
                      availableFrom: availableFrom,
                      through: through,
                      complete: reasons.isEmpty,
                      reasons: Array(Set(reasons)).sorted())
    }

    // MARK: - Contract shapes

    /// `findings[]` as Contract v1 Finding objects. Evidence is mandatory in
    /// the contract; every engine supplies it, and if one ever did not, the
    /// explanation is the only honest stand-in — never an empty list.
    static func findingsJSON(_ findings: [Finding]) -> [[String: Any]] {
        findings.map { finding in
            var out: [String: Any] = [
                "severity": finding.severity.rawValue,
                "title": finding.title,
                "explanation": finding.explanation,
                "evidence": finding.evidence.isEmpty ? [finding.explanation] : finding.evidence,
                "confidence": finding.confidence,
            ]
            if let rec = finding.recommendation { out["recommendation"] = rec }
            return out
        }
    }

    /// `incidents[]` as Contract v1 Incident objects, newest first.
    static func incidentsJSON(_ incidents: [Incident]) -> [[String: Any]] {
        incidents.map { incident in
            var out: [String: Any] = [
                "start": iso(incident.start),
                "devicesLost": incident.lostDevices.map { device -> [String: Any] in
                    var d: [String: Any] = ["name": device.name]
                    if let vidPid = device.vidPid { d["vidPid"] = vidPid }
                    return d
                },
            ]
            if let end = incident.end { out["end"] = iso(end) }
            if incident.rootEventCount > 0 { out["rootEvent"] = "linkDown" }
            if let parent = incident.sharedParentLocationID {
                out["sharedParent"] = String(format: "usb:0x%08X", parent)
            }
            if let mw = incident.peakDischargeMilliwatts {
                out["power"] = ["peakDischargeMilliwatts": mw]
            }
            return out
        }
    }

    /// `analysis{}`: window, coverage, baseline state and capabilities.
    static func analysisJSON(_ result: Result) -> [String: Any] {
        var coverage: [String: Any] = [
            "availableFrom": iso(result.availableFrom),
            "through": iso(result.through),
            "complete": result.complete,
        ]
        if !result.reasons.isEmpty { coverage["reasons"] = result.reasons }
        return [
            "windowHours": result.windowHours,
            "generatedAt": iso(result.generatedAt),
            "coverage": coverage,
            // TBDoctor has no known-good baseline yet (align-cli-verbs adds
            // `baseline save`); say so rather than implying health.
            "baseline": ["state": "no-baseline"],
            // Link drops come from `log stream` kernel predicates.
            "capabilities": ["linkEvents": "kernel"],
        ]
    }

    private static func iso(_ date: Date) -> String { ISO8601DateFormatter().string(from: date) }
}
