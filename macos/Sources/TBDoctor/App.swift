import SwiftUI

@main
struct TBDoctorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var collector = Collector.shared

    init() {
        let arguments = Array(CommandLine.arguments.dropFirst())
        // --inspect needs a window, so it is handled by the delegate rather than
        // exiting here like the other terminal modes.
        if !arguments.contains("--inspect"), Headless.run(arguments) { exit(0) }
        _ = Inspect.parse(arguments)
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarView().environmentObject(collector)
        } label: {
            Image(systemName: Health.symbol(for: collector.health))
        }
        .menuBarExtraStyle(.window)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Menu bar only — no Dock icon, and no window on launch. Collection has
        // to begin here rather than in a view's onAppear: the whole point is to
        // be recording before anyone thinks to open the UI.
        NSApp.setActivationPolicy(.accessory)
        MainActor.assumeIsolated {
            // Inspecting a recording is a read-only, one-window mode: it must not
            // start collecting, or it would contend for the store lock with a
            // real instance already running on this machine.
            if let recorded = Inspect.sample {
                Inspect.present(recorded)
                return
            }
            Collector.shared.start()
        }
    }

    /// Launching the app again while it is already running opens the timeline
    /// instead of doing nothing. This is the escape hatch when the menu bar icon
    /// cannot be found — `open TBDoctor.app` always gets you a window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        MainActor.assumeIsolated { TimelineWindow.shared.show() }
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated { Collector.shared.stop() }
    }
}
