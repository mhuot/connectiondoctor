## ADDED Requirements

### Requirement: Release artifacts carry the product name
macOS release artifacts SHALL be named `ConnectionDoctor-<version>.dmg` and `ConnectionDoctor-<version>.zip`, matching the Windows executables and the repository.

#### Scenario: Reading a release page
- **WHEN** someone opens a release
- **THEN** every artifact names the same product, and the notes state once that the macOS collector was formerly TBDoctor
