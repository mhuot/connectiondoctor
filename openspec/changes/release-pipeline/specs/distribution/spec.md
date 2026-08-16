## ADDED Requirements

### Requirement: One release per tag, from one commit
Pushing a tag `vX.Y.Z` SHALL produce a GitHub Release containing `TBDoctor-X.Y.Z.dmg`, `TBDoctor-X.Y.Z.zip`, `ConnectionDoctor-X.Y.Z-win-arm64.exe`, `ConnectionDoctor-X.Y.Z-win-x64.exe` and `SHA256SUMS`, all built from that commit with one dashboard build shared by both collectors.

#### Scenario: Tag pushed
- **WHEN** `v0.1.0` is pushed
- **THEN** the release exists with the five artifacts and each collector's `version` reports `0.1.0` and the same dashboard bundle version

### Requirement: Signed and notarized when secrets exist, honest when not
The macOS artifacts SHALL be Developer ID signed and notarized, and the Windows executables Authenticode-signed via SignPath, whenever the corresponding secrets are configured; when they are not, the job SHALL still produce unsigned artifacts and state in its summary that signing was skipped.

#### Scenario: Clean Mac
- **WHEN** a user on macOS Sequoia downloads the DMG from a signed release and drags the app to Applications
- **THEN** it opens without a Gatekeeper block

#### Scenario: Fork without secrets
- **WHEN** the workflow runs in a fork
- **THEN** it completes, uploads unsigned artifacts, and the summary reads "signing skipped: secret X not set"

### Requirement: Single-file Windows executables
Windows artifacts SHALL be self-contained single-file executables for win-arm64 and win-x64 that run with no .NET runtime installed and support `install` from any location.

#### Scenario: Run from Downloads
- **WHEN** the exe is run from the Downloads folder on a machine without .NET 8
- **THEN** `probe` works, and `install` registers that path at login

### Requirement: CI on every change
Pull requests and pushes to `main` SHALL run the dashboard tests and build, the macOS build with the staged bundle, and the Windows build and tests with the staged bundle.

#### Scenario: Broken embed
- **WHEN** a change breaks the bundle's relative asset paths
- **THEN** CI fails before a tag can be cut
