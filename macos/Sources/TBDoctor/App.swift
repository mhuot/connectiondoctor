import SwiftUI

@main
struct TBDoctorApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var collector = Collector.shared

    init() {
        // Runs before any scene is built, so a terminal invocation never flashes
        // a menu bar item or takes over the run loop.
        if Headless.run(Array(CommandLine.arguments.dropFirst())) { exit(0) }
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
        MainActor.assumeIsolated { Collector.shared.start() }
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
