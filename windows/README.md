# Claude Graft for Windows

This is the Windows port of Claude Graft. It does what the macOS app does — runs
extra Claude Desktop profiles side by side, each with its own login, and lets
one profile borrow another's Claude Code chats — from a notification-area icon
rather than the menu bar.

Claude Desktop lays a profile out the same way on both systems, folder for
folder, so the reasoning the macOS app is built on carries across unchanged. The
store is `claude-code-sessions/<account>/<org>` and `local-agent-mode-sessions`,
keyed the same way, with the same `skills-plugin` and shortened-uuid quirks; the
port reads it under `%APPDATA%\Claude` where the Mac reads it under Application
Support. Two things differ underneath, and only two. A graft links a profile's
chat directory onto the source's, and where the Mac makes a symlink Windows
makes a directory junction — measured here holding every property the graft
depends on, made without administrator rights. And where the Mac borrows the
usage token out of the keychain behind a per-build ACL, Windows keeps the same
Chromium safe-storage key in the profile's own `Local State`, wrapped with DPAPI
to the logged-in user, which unwraps with no dialog — so the whole keychain
prompting dance has no counterpart here.

## Layout

The core is a plain library with no UI in it, so both the app and the launcher
stub reach the same graft, and the tests read the same logic:

- `ClaudeGraft.Core` — the graft itself. Profile identity, the stash that makes
  a graft reversible, the record sweep that recovers sessions whose transcripts
  outlived their sidebar records, the chat-history mirror that merges two
  histories and carries changes both ways, which Claude is running and where,
  the borrowed usage token, and the plan-usage figures. This is where every
  invariant the macOS `GraftCore` documents lives, translated with the reasoning
  intact.
- `ClaudeGraft` — the WinUI 3 app: the tray icon and its menu, and the manager
  window that lists each profile as a card, adds and edits and removes them, and
  shows each account's five-hour and weekly usage.
- `GraftLaunch` — a standalone launcher stub a desktop shortcut points at. Given
  a profile folder it brings the storage in line, files any missing records, and
  opens Claude on the profile, without the tray app running — the Windows echo
  of the copy of the launcher each macOS bundle carries.
- `ClaudeGraft.Tests` — the checks, which read as sentences the way the macOS
  suite does. They cover the parts where getting a decision backwards loses a
  chat: the mirror's whole decision table, both shapes of a stash, the record
  sweep end to end, the usage parsers, the crypto round trip.

## Building and running

The core and the tests are plain `net10.0` and build and run anywhere:

    dotnet test windows/ClaudeGraft.Tests

The app is WinUI 3 and needs the Windows App SDK toolchain — the .NET 10 SDK,
Developer Mode, and the WinApp CLI. To build and run it:

    winapp run windows/ClaudeGraft --arch x64

Opening a profile from the app copies the launcher stub into a stable per-user
folder and writes a desktop shortcut pointing at it, so the profile goes on
opening after the app updates.

## Left to the maintainer

The port is the code, not a decision about how it ships. Distribution and
signing belong to whoever owns this repository, since they turn on keys and
channels a contributor does not have, so the port leaves them alone:

- **Distribution and updates.** The MSIX build wired up here is a working
  signing dry-run, but a full-trust utility that reaches across profile
  directories and reads another app's credentials fits an unpackaged installer
  better than a sandboxed package — the same shape Claude Desktop itself uses on
  Windows, and the sibling of the Sparkle-and-Homebrew story on the Mac. Which
  installer, which update feed, and whether a release is signed at all are the
  maintainer's call.
- **The manifest identity** is still the scaffold's placeholder name and
  publisher. A real release sets its own.
- **Signing in a second account** works, with one rough edge: the OAuth callback
  comes back through the `claude://` handler, which Windows routes to the default
  profile rather than the grafted instance the person is using. A small handler
  shim that forwards the callback to the foreground profile would fix it — it
  modifies a system registration shared with the real Claude, so it is left as a
  deliberate choice rather than done quietly.
- **Two smaller parity items** remain against the Mac: carrying each shortcut's
  own config the way a bundle carries `graft.json`, so the bundle wins where it
  and the list disagree; and writing the state report the Mac leaves for
  diagnosing an incident after the fact.

## What has not been proven

One behavioural question is open. On macOS, Claude refuses to write a session
record into a folder that resolves outside the profile, which is the whole
reason the record sweep exists. Whether Windows Claude does the same through a
junction has not been tested, because it needs Claude Desktop fully quit and a
signed-in throwaway profile at once. The port assumes it behaves as macOS does
and ports the sweep in full; if Windows turns out more lenient, the sweep is
simply doing no harm.
