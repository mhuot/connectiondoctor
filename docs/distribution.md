# Distribution

> **Status: proposed** — introduced by `openspec/changes/release-pipeline`.

The install story is the same on both platforms, and it is deliberately thin:
**download one file → open it → it registers itself at login → the dashboard
is at `http://localhost:8787` from now on.** The binary is its own installer
(`install` / `uninstall` / `status`, see [`cli.md`](cli.md)); the packaging
around it exists only to get the file onto the machine in a form the OS trusts.

## Artifacts per release

One GitHub Release per tag `vX.Y.Z`, built by CI from that tag (see
"Release pipeline"), containing:

| Platform | Artifact | Why this form |
|---|---|---|
| macOS (arm64 + x86_64 universal) | `TBDoctor-X.Y.Z.dmg` — drag to Applications | What Mac users expect for an app; opens, drag, done. Signed with Developer ID and **notarized**, so Gatekeeper opens it without ceremony. |
| macOS | `TBDoctor-X.Y.Z.zip` — the same `.app` | For automation: Homebrew cask, scripted installs, future in-app updates. Same signature and notarization ticket. |
| Windows arm64 | `ConnectionDoctor-X.Y.Z-win-arm64.exe` | **Single-file, self-contained** (.NET runtime inside). No prerequisite; runs from a USB stick or a download folder; `install` when you want it resident. Authenticode-signed via SignPath. |
| Windows x64 | `ConnectionDoctor-X.Y.Z-win-x64.exe` | Same. |
| all | `SHA256SUMS` | Checksums for every artifact above, for casks/manifests and for anyone who wants to verify. |

Both binaries answer `version` with the tag, the commit, and the version of the
dashboard bundle compiled into them, and the envelope carries an optional
`producer: { name, version }` so a fleet dashboard can show which collector
build produced what.

### What we deliberately do not ship (yet)

- **MSI / MSIX.** Everything an MSI would do is the `install` verb; an unsigned
  MSI hits SmartScreen exactly like an unsigned exe. MSIX would force package
  identity, a containerised registry and a trusted sideloading cert for a
  per-user tray tool. Revisit MSI (WiX v5) only if `winget` or Group Policy
  distribution is wanted — it would just drop the exe under
  `%LOCALAPPDATA%\Programs\ConnectionDoctor` and run `install`.
- **macOS `.pkg`.** Warranted only for a system-wide LaunchDaemon or writes
  outside the bundle. TBDoctor is per-user (menu bar, `~/Library/Application
  Support`), registers its own login item via `SMAppService`, and offers the
  CLI as a symlink — no pkg needed.
- **Auto-update.** A `version` verb and a "newer release available" note in the
  dashboard come first; Sparkle (macOS) / a self-replacing exe (Windows) later,
  if the fleet ever gets big enough to want it.

## Signing

Signing, not packaging, is what makes "open one file" true for anyone but the
developer:

| | Without | With |
|---|---|---|
| macOS (Sequoia+) | Gatekeeper blocks; user must find *Privacy & Security → Open Anyway* | Opens like any app |
| Windows | SmartScreen "unknown publisher → More info → Run anyway" (exe **and** MSI) | Opens; reputation accrues to the certificate |

- **macOS:** Apple Developer Program (in place). CI signs with a **Developer ID
  Application** certificate (installed into a temporary keychain from a
  base64 secret), enables the hardened runtime, submits with `notarytool
  --wait`, and staples the ticket to the `.app` before making the DMG and zip.
  Local `build_app.sh` keeps ad-hoc signing for development.
- **Windows:** **SignPath Foundation** open-source programme (application in
  progress). CI uploads the unsigned single-file exes as artifacts and submits a
  signing request via `signpath/github-action-submit-signing-request`; the
  signed exes come back into the workflow and are what get published. Signing
  is a release-tag-only policy; PR builds stay unsigned.
- Until a secret is configured, the corresponding job **skips** signing (and
  says so in the job summary) rather than failing — a fork or a dry run still
  produces usable, unsigned artifacts.

## Release pipeline

`.github/workflows/release.yml`, on push of a `v*` tag. Three build jobs share
one dashboard build so the bundle and the collectors can never be from
different commits:

```
dashboard (ubuntu)   npm ci → npm test → npm run build → upload dist/
   ├─▶ macos (macos-15)   download dist → stage → build_app.sh VERSION=tag → sign → notarize → staple → .dmg + .zip
   └─▶ windows (windows)  download dist → stage → dotnet test → dotnet publish ×2 (single-file, self-contained) → SignPath
release (ubuntu)     SHA256SUMS → gh release create vX.Y.Z with every artifact, notes from the tag message
```

`.github/workflows/ci.yml` runs the same three builds (unsigned, no release) on
every pull request and push to `main`, so a broken build never reaches a tag.

Version flow: the tag is the single source. `build_app.sh` writes it into
`CFBundleShortVersionString`; `dotnet publish -p:Version=`; both binaries and
the dashboard read it back for `version` / `producer.version`. `main` builds
between tags are `X.Y.Z-dev+<sha>`.

## Getting it onto machines

Today: download from the GitHub Release. Planned once releases are regular:

- **Homebrew cask** `tbdoctor` — pulls the `.zip`, installs to
  `/Applications`, and its `binary` stanza symlinks
  `/usr/local/bin/tbdoctor` for the CLI/MCP.
- **winget** manifest `mhuot.ConnectionDoctor` — pointing at the signed exe;
  winget's `portable` type fits a self-installing single exe with no MSI.

Both are metadata that reference the release artifacts and checksums above,
which is why `SHA256SUMS` ships from day one.
