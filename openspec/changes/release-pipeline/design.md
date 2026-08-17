# Design: release-pipeline

## Shape
```
dashboard (ubuntu-latest)      npm ci → npm test → npm run build → upload-artifact dist
macos     (macos-15)           needs dashboard → download → stage → build_app.sh VERSION → sign → notarize → dmg+zip
windows   (windows-latest)     needs dashboard → download → stage → dotnet test → publish arm64,x64 → SignPath
release   (ubuntu-latest)      needs macos,windows → SHA256SUMS → gh release create
```
`ci.yml` reuses the first three jobs (via a reusable workflow
`build.yml` with `inputs.release: false`) so PR builds and release builds are
the same steps.

## macOS
- **Architecture:** arm64 only (`swift build -c release` on the arm64
  runner), and the job asserts it with `lipo -info` so the shipped artifact
  matches this sentence (issue #23; `docs/distribution.md` says the same). The
  fleet is Apple silicon; a universal binary doubles build time. Revisit if an
  Intel user appears; the workflow has one line to change (`--arch arm64
  --arch x86_64`) and the assertion flips with it.
- **Signing:** `DEVELOPER_ID_CERT_P12` (base64) + `DEVELOPER_ID_CERT_PASSWORD`
  imported into a temporary keychain; `codesign --deep --options runtime
  --timestamp -s "Developer ID Application: …"`; entitlements none beyond
  hardened runtime (no JIT, no unsigned memory).
- **Notarization:** `xcrun notarytool submit --wait` with an App Store Connect
  API key (`AC_API_KEY_ID`, `AC_API_ISSUER_ID`, `AC_API_KEY_P8`), then
  `xcrun stapler staple`. Zip first for submission, staple the `.app`, then
  build the DMG from the stapled app so the DMG contents carry the ticket.
- **DMG:** `hdiutil create` from a folder holding `TBDoctor.app` and an
  `Applications` symlink; no custom background (avoid a design asset that
  drifts). Sign the DMG too.
- **Local dev unchanged:** `build_app.sh` still ad-hoc signs when no identity
  is given.

## Windows
- `dotnet publish -c Release -r win-{arm64,x64} --self-contained
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  -p:Version=$VERSION`. WinForms (tray) is compatible with single-file. Size
  ~70 MB each; acceptable for "download one file".
- **SignPath:** `signpath/github-action-submit-signing-request` with
  `SIGNPATH_API_TOKEN`, organization/project/policy ids as variables;
  `wait-for-completion: true`; the signed exes replace the unsigned artifacts.
  Skipped when the token is absent (fork/dry run), with a job-summary line.
- Tests run on the Windows runner (`dotnet test`), where the SetupAPI-facing
  code can at least load; the `EmbeddedUiTests` become real once the bundle is
  staged in the same job.

## Version flow
Tag `vX.Y.Z` → `VERSION=X.Y.Z` env in every job. `main` builds:
`0.0.0-dev+<sha7>`. Dashboard `package.json` version is set at build time
(`npm version --no-git-tag-version $VERSION`) and shown in the app footer;
collectors expose it via `version` and `producer.dashboard`. Envelope
`producer: {name: "tbdoctor"|"connectiondoctor", version, commit?, dashboard?}`.

## Release notes
`gh release create vX.Y.Z --notes-from-tag`; the tag message is the changelog
(annotated tags). Artifacts named exactly as in `docs/distribution.md`.

## Secrets inventory (repository → Settings → Secrets)
`DEVELOPER_ID_CERT_P12`, `DEVELOPER_ID_CERT_PASSWORD`, `AC_API_KEY_ID`,
`AC_API_ISSUER_ID`, `AC_API_KEY_P8`, `SIGNPATH_API_TOKEN`; variables
`SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`,
`DEVELOPER_ID_IDENTITY` (the "Developer ID Application: Name (TEAMID)" string).
