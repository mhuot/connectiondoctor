# Proposal: add-tauri-shell

## Why
The dashboard is web-first by design (recorded in add-react-dashboard). The
follow-up was always a thin native shell: system webview, ~10MB, tray-capable,
ARM64 on both target platforms — so the fleet dashboard is an app you launch,
not a dev server you start.

## What
- Tauri v2 shell wrapping the existing Vite app unchanged (no shell APIs in
  app code — the web build must keep working in a plain browser).
- macOS app bundle produced by `tauri build`; Windows deferred until there is
  a Windows build machine in the loop.

## Non-goals
Tray icon, sidecar collector management, auto-update — future changes. The
native TBDoctor/ConnectionDoctor apps still own the always-on presence.

## Impact
New `app/src-tauri/` (Rust); capability `desktop-shell`.
