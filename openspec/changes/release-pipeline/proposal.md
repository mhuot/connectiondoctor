# Proposal: release-pipeline

## Why
Nobody but the developer can install this today: the macOS app is ad-hoc
signed (Gatekeeper blocks it on Sequoia), the Windows build is
framework-dependent (needs .NET 8 pre-installed) and unsigned (SmartScreen),
and neither is downloadable — every README's "Next" already lists "a release
pipeline so each collector ships a downloadable binary". The monorepo makes it
one pipeline: the dashboard bundle and both collectors are cut from one commit
and can never drift.

## What
- `docs/distribution.md`: artifacts, why not MSI/MSIX/pkg, signing, version
  flow, later Homebrew/winget.
- `.github/workflows/ci.yml`: on PR and push to `main` — dashboard test+build
  (ubuntu), TBDoctor build with the staged bundle (macos-15), ConnectionDoctor
  build+test with the staged bundle (windows-latest). Unsigned, no artifacts
  published.
- `.github/workflows/release.yml`: on tag `v*` — same three builds, then
  macOS: Developer ID sign, `notarytool` submit+staple, `.dmg` + `.zip`;
  Windows: `dotnet publish` single-file self-contained for win-arm64 and
  win-x64, SignPath signing request; `SHA256SUMS`; GitHub Release with notes
  from the tag message. Signing steps **skip with a visible summary** when
  their secrets are absent, so forks and dry runs still produce artifacts.
- Version flow from the tag: `build_app.sh` takes `VERSION`, csproj takes
  `-p:Version=`; both binaries answer `version`; the envelope gains optional
  `producer: {name, version}` (additive).
- Apache-2.0 `LICENSE` at the root (prerequisite for the SignPath OSS
  programme and for a public release).

## Non-goals
Auto-update; MSI/MSIX/pkg; Homebrew cask and winget manifests (follow-ups
once releases are regular; `SHA256SUMS` exists so they can be added without
changing the pipeline); macOS is arm64-only (decided in design; universal is
a one-line change when an Intel user appears).

## Impact
Capability `distribution` (new); `contract-v1` (producer field);
`.github/workflows/`, `macos/build_app.sh` (VERSION, signing identity env),
`windows/…csproj` (single-file publish props), `dashboard/package.json`
(version from tag), `LICENSE`, READMEs (download section).
