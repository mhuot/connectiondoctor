import SwiftUI
import AppKit

/// The connection diagram gets its own resizable window rather than a split
/// pane: the wider layouts run past 1600pt, which a ~540pt pane cannot show
/// without constant horizontal scrolling.
@MainActor
final class ConnectionsWindow {
    static let shared = ConnectionsWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let created = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 1120, height: 760),
                styleMask: [.titled, .closable, .miniaturizable, .resizable],
                backing: .buffered,
                defer: false)
            created.title = "Connections"
            created.center()
            created.setFrameAutosaveName("TBDoctorConnections")
            created.minSize = NSSize(width: 620, height: 420)
            // Accessory apps outlive their windows; without this the window is
            // deallocated on close and reopening crashes.
            created.isReleasedWhenClosed = false
            created.contentView = NSHostingView(rootView: ConnectionsRoot())
            window = created
        }

        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

private struct ConnectionsRoot: View {
    @ObservedObject private var collector = Collector.shared

    var body: some View {
        if let sample = collector.current {
            DiagramView(sample: sample) { collector.refreshNow() }
        } else {
            ContentUnavailableView("Nothing enumerated yet", systemImage: "cable.connector",
                                   description: Text("The diagram appears within a few seconds of launch."))
        }
    }
}
