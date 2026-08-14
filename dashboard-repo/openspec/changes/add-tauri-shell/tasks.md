# Tasks: add-tauri-shell
- [x] 1.1 Install Rust toolchain; add @tauri-apps/cli; scaffold src-tauri with Vite integration
- [x] 1.2 Configure: app id, window defaults, http allowlist open (collector endpoints are arbitrary LAN hosts)
- [ ] 1.3 tauri build → macOS .app; launch and verify render
  - .app builds (12MB) and the process runs stably; window render NOT yet verified —
    display was asleep during the build session. One manual launch confirms it.
  - DMG bundling fails headlessly (bundle_dmg.sh drives Finder); .app is the artifact.
