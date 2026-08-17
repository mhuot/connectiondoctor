# Tasks: retire-tbdoctor-name
- [ ] 1.1 SwiftPM target and directory → `ConnectionDoctor`; `build_app.sh` builds `ConnectionDoctor.app` with the new executable and bundle id
- [ ] 1.2 Data directory: `Store.directory` → `…/ConnectionDoctor`, one-time migration from the old path when the new one is absent, `CONNECTIONDOCTOR_DIR` with `TBDOCTOR_DIR` still honoured
- [ ] 1.3 `tbdoctor` compatibility symlink beside the binary; deprecation notice on stderr when invoked through it
- [ ] 1.4 `scripts/build-ui.{sh,ps1}` stage into `macos/Sources/ConnectionDoctor/ui`; `.github/workflows/build.yml` paths and artifact names (`ConnectionDoctor-<v>.dmg`/`.zip`), `lipo` assertion path
- [ ] 1.5 Docs: `cli.md` names one binary; `mcp.md`, `distribution.md`, `architecture.md`, both READMEs, dashboard copy; one "formerly TBDoctor" line
- [ ] 1.6 Verify: build, serve, `/contract` on macOS; migration from a populated old directory; `tbdoctor` alias still runs
