import SwiftUI
import AppKit

/// The timeline window is built in AppKit rather than declared as a SwiftUI
/// `Window` scene so it can be opened programmatically from anywhere —
/// the menu bar item, a Dock/Finder reopen, or a second launch.
///
/// This matters more than it sounds: the app is LSUIElement, so it has no Dock
/// icon and no app-switcher entry. If the menu bar icon is hidden (on a notched
/// display, menu bar items silently overflow *behind the notch*), a
/// SwiftUI-scene-only window leaves no way to reach the UI at all.
@MainActor
final class TimelineWindow {
    static let shared = TimelineWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let created = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 1000, height: 620),
                styleMask: [.titled, .closable, .miniaturizable, .resizable],
                backing: .buffered,
                defer: false)
            created.title = "Thunderbolt Timeline"
            created.center()
            created.setFrameAutosaveName("TBDoctorTimeline")
            // Accessory apps outlive their windows; without this the window is
            // deallocated on close and reopening crashes.
            created.isReleasedWhenClosed = false
            created.contentView = NSHostingView(
                rootView: TimelineView().environmentObject(Collector.shared))
            window = created
        }

        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
