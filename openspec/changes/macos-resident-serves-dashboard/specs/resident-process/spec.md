## ADDED Requirements

### Requirement: The resident process serves the dashboard
On both platforms the resident process (macOS menu-bar app, Windows tray) SHALL run the recorder and serve the dashboard on the default port for the whole login session, and SHALL open the browser to it from its menu.

#### Scenario: macOS after login
- **WHEN** TBDoctor is registered as a login item and the user logs in
- **THEN** `http://localhost:8787` serves the dashboard with this Mac already loaded, with no terminal involved

#### Scenario: Port already held
- **WHEN** a `serve` process or another instance already holds the port
- **THEN** the resident process keeps recording and its "Open dashboard…" opens the existing server

### Requirement: No second UI
The resident process SHALL NOT draw topology, timeline or inspector views natively; its menu SHALL be limited to status, the leading root cause, the last incident, "Open dashboard…" and Quit.

#### Scenario: Looking for the Connections window
- **WHEN** a user of the previous release looks for "Connections…" in the menu bar
- **THEN** "Open dashboard…" leads to the same topology in the browser, and the README says so
