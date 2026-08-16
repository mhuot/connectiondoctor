import Foundation
import SwiftUI
import AppKit

/// Opens the connection tree for a *recorded* sample rather than live hardware.
///
/// Useful for looking at a capture from another machine — a dock that misbehaves
/// on one Mac and not another is a common shape for this class of fault, and the
/// two trees side by side are the fastest way to see why.
enum Inspect {
    private(set) static var sample: Sample?

    /// Parses `--inspect <path>`. The path may be a samples.jsonl (the last
    /// record is used) or a single JSON object.
    static func parse(_ arguments: [String]) -> Bool {
        guard let index = arguments.firstIndex(of: "--inspect"), index + 1 < arguments.count else { return false }
        let path = arguments[index + 1]

        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else {
            FileHandle.standardError.write("TBDoctor: cannot read \(path)\n".data(using: .utf8)!)
            exit(1)
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        // Walk backwards: the most recent record is the interesting one.
        for line in text.split(separator: "\n").reversed() {
            if let data = line.data(using: .utf8), let decoded = try? decoder.decode(Sample.self, from: data) {
                sample = decoded
                return true
            }
        }

        FileHandle.standardError.write("TBDoctor: no decodable sample in \(path)\n".data(using: .utf8)!)
        exit(1)
    }

    @MainActor
    static func present(_ sample: Sample) {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1120, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = "Connections — recorded \(Diagnosis.stamp(sample.t))"
        window.center()
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: DiagramView(sample: sample))
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }
}
