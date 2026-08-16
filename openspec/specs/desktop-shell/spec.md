# desktop-shell Specification

## Purpose
TBD - created by archiving change add-tauri-shell. Update Purpose after archive.
## Requirements
### Requirement: Native shell wraps the unchanged web app
The system SHALL package the dashboard as a desktop app whose web code is identical to the browser build, with no shell-API dependencies in application code.

#### Scenario: Browser build still works
- **WHEN** the web app is built without the shell
- **THEN** all views function identically (file drop, fixtures, HTTP sources)

#### Scenario: Desktop launch
- **WHEN** the packaged app is launched
- **THEN** the dashboard renders and can reach collector HTTP endpoints on the local network

