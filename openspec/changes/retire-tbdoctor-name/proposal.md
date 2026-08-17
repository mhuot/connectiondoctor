# Proposal: retire-tbdoctor-name

## Why
`windows/README.md` already argues the case: *ConnectionDoctor is preferred over
TBDoctor-Windows or USBDoctor — it describes the user problem rather than
prematurely blaming one protocol.* That reasoning applies to macOS identically,
and today it is applied unevenly. TBDoctor's headline finding is a **power**
deficit; its second is a **USB hub** branch. "TB" names the one component that
is most often innocent, in a tool whose entire thesis is not blaming a protocol
before the evidence does.

It is also the last split in an otherwise unified product: the repo, the MCP
server name (`connectiondoctor`), the tool names (`connection_*`), the contract,
the CLI verb set and the dashboard are all common. `docs/cli.md` has to write
"`tbdoctor` (macOS) and `connectiondoctor` (Windows)" in every sentence — a tax
paid per document, per reader.

Cost now versus later: 0 stars, 0 forks, one pre-release, no Homebrew cask, no
winget manifest, and the only live registrations are on this fleet's own
machines. Every future release, package manifest, screenshot and third-party
config makes the rename more expensive. This is the cheapest it will ever be.

## What
- macOS ships **`ConnectionDoctor.app`** (executable `ConnectionDoctor`, bundle
  id `net.mhuot.connectiondoctor`); the CLI is `connectiondoctor` on both
  platforms, so `docs/cli.md` stops naming two binaries.
- **Compatibility where it is possible, honesty where it is not.** Preserved:
  - recorded history and baselines — `~/Library/Application Support/TBDoctor`
    is migrated to `…/ConnectionDoctor` on first run when the new directory
    does not yet exist;
  - `TBDOCTOR_DIR` keeps working alongside `CONNECTIONDOCTOR_DIR`;
  - a `tbdoctor` alias **on PATH** for one release, printing a one-line notice
    on stderr, so scripts and shell history keep working.

  **Broken deliberately:** any command naming the old bundle by absolute path
  — `…/TBDoctor.app/Contents/MacOS/TBDoctor` — including an MCP registration
  made from the README. `TBDoctor.app` no longer exists, and no symlink inside
  the new bundle can rescue a path to a bundle that is gone. A shim
  `TBDoctor.app` was considered and rejected: it would need its own signing and
  notarization, and a DMG containing two apps teaches exactly the confusion
  this change removes. The fix is one line, it is in the release notes, and
  `align-cli-verbs` requires re-registering anyway (`--mcp` becomes `mcp`).
- Release artifacts become `ConnectionDoctor-<version>.dmg` / `.zip`.
- The Swift target and source directory become `ConnectionDoctor`;
  `macos/Sources/TBDoctor/ui` → `macos/Sources/ConnectionDoctor/ui`, with
  `scripts/build-ui.{sh,ps1}` following.
- READMEs keep the origin story and screenshots; one "formerly TBDoctor" line
  appears in the README and the v0.2.0 notes, then goes away.

## Non-goals
Rewriting archived OpenSpec changes or git history — they are a record of what
was true when written, and `git log --follow` still crosses the rename.
Renaming the *product story*: the CalDigit dock investigation, the findings and
the screenshots stay.

## Impact
`macos/` (target, bundle, data directory, ~20 files), `scripts/build-ui.*`,
`.github/workflows/build.yml` (staging path, artifact names),
`docs/{cli,mcp,distribution,architecture}.md`, both READMEs, dashboard copy.
Capabilities `cli`, `distribution`.
