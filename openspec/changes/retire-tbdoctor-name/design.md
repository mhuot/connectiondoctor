# Design: retire-tbdoctor-name

## What the user has to do: two things, both one-liners
The rename is only worth doing if the data survives untouched and the required
actions are few, named, and in the release notes. They are:

1. **Re-register the MCP server** if it was registered against the old bundle
   path (`claude mcp remove tbdoctor` then the new `claude mcp add` line —
   `align-cli-verbs` requires this anyway when `--mcp` becomes `mcp`).
2. **Remove the old Login Item**, if the old app was registered at login.

Everything else is preserved, and the second action is automated where it can
be: when the migration runs and finds a login-item registration for
`net.mhuot.tbdoctor`, it unregisters it via `SMAppService` before registering
the new one, and says so on stderr. It cannot do that if the old app bundle has
already been deleted, in which case the stale entry is harmless (macOS drops a
login item whose bundle is gone) and the release notes say where to look.

Everything below is what is preserved without any action:

| Thing they have | What happens |
|---|---|
| Recorded history and baseline in `~/Library/Application Support/TBDoctor` | Migrated on first run when the new directory is absent (a directory move, then a marker so it is never attempted twice). If both exist, the new one wins and the old is left untouched — never merged, never deleted. |
| `TBDOCTOR_DIR` in a script | Still honoured; `CONNECTIONDOCTOR_DIR` takes precedence when both are set. |
| `claude mcp add tbdoctor -- …/TBDoctor.app/Contents/MacOS/TBDoctor --mcp` | **Breaks, deliberately.** The bundle is gone and no symlink inside the new one rescues an absolute path to it. A shim `TBDoctor.app` would need its own signing and would put two apps in the DMG — teaching the confusion this change removes. One line to re-register, in the release notes; `align-cli-verbs` requires re-registering regardless (`--mcp` → `mcp`). The `tbdoctor` **PATH** alias still covers scripts and shell history. |
| Login Items entry | The bundle id changes, so macOS treats it as a new app: `install` re-registers, and `uninstall` on the old app is a one-liner in the release notes. |

## Bundle identifier
`net.mhuot.tbdoctor` → `net.mhuot.connectiondoctor`. This is the one
irreducible break: macOS keys login items, TCC and preferences on it. Doing it
now, with one pre-release out and no external users, is the cheapest possible
moment; doing it after a signed, notarized release with a Homebrew cask would
mean supporting both identities.

## What keeps the old name
Archived OpenSpec changes, git history, and the commit that performs the rename.
The story in `macos/README.md` — the CalDigit Element Hub, the power supply that
could not cover demand — is the reason the tool exists and is not a naming
artifact; it stays, with the product name updated around it.
