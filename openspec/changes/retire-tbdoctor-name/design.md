# Design: retire-tbdoctor-name

## What the user actually has to do: nothing
The rename is only worth doing if it costs the person running it nothing, so:

| Thing they have | What happens |
|---|---|
| Recorded history and baseline in `~/Library/Application Support/TBDoctor` | Migrated on first run when the new directory is absent (a directory move, then a marker so it is never attempted twice). If both exist, the new one wins and the old is left untouched — never merged, never deleted. |
| `TBDOCTOR_DIR` in a script | Still honoured; `CONNECTIONDOCTOR_DIR` takes precedence when both are set. |
| `claude mcp add tbdoctor -- …/TBDoctor.app/Contents/MacOS/TBDoctor --mcp` | The old path is gone, so the registration must be updated — but a `tbdoctor` symlink beside the new binary keeps the *command* working, and `docs/mcp.md` already says the server is `connectiondoctor`. The README shows the new line. |
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
