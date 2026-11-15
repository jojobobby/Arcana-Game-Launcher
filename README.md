# Arcana Launcher

The Windows launcher for **Arcana**, and the only way to get the game: it installs the native client, keeps it in
step with the server, and starts it. It keeps itself up to date too.

## Download

**[Download Arcana Launcher](https://tidangames.com/download/windows)** (the website redirects to
`ArcanaLauncherSetup.exe` on this repo's latest release).

Run `ArcanaLauncherSetup.exe`. It installs Arcana Launcher for your Windows account, with no admin rights, into
`%LocalAppData%\Programs\Arcana Launcher`, adds a Start menu shortcut (and a desktop one, unless you untick it) and an
entry in Windows' Apps list. Then open Arcana Launcher and click **Install**.

The launcher is not code-signed yet, so on first run Windows may say it protected your PC: click **More info**, then
**Run anyway**.

Windows 10 or 11, 64-bit.

## Where things go

| What | Where |
|---|---|
| The launcher | `%LocalAppData%\Programs\Arcana Launcher\ArcanaLauncher.exe` |
| The game | `%LocalAppData%\Programs\Arcana Launcher\Arcana\` (Live) and `Arcana-development\` (Development) |
| Live or Development | `%LocalAppData%\Arcana Launcher\settings.json` (the last one picked) |
| Launcher log | `%LocalAppData%\Arcana Launcher\logs\launcher.log` (the **Open logs** link) |
| Downloads | `%LocalAppData%\Arcana Launcher\downloads\` (game zips by SHA-256; the last two are kept) |
| The game's own settings and data | `%LocalAppData%\Arcana\` and `%AppData%\Arcana\Arcana\` |

Uninstall from Windows' Apps list. It removes the launcher, the game files and the launcher's data, then asks whether
to remove the game's own settings and data too (default: keep them).

## What it does

- **Play** checks the server's build, installs, updates or repairs the game when needed, starts it and closes. The
  game server admits only the client build it serves, so an outdated client is never played.
- **Update** does the same without starting the game. **Verify Files** hashes every game file against the release's
  file list and repairs what differs. **Uninstall** removes the game files (the launcher stays).
- Downloads resume where they stopped (a cut connection, Cancel, closing the launcher), retry a busy or unreachable
  server, give up on a stall after 30 s and try again, and are checked file by file before anything is replaced: a bad
  download never touches a working install.
- It waits for a running game to close before updating it, checks free disk space first, and puts the game back exactly
  as it was if anything fails.
- It updates itself on every start, from this repo's latest release. If the new version does not start, it puts the
  old one back and carries on.
- One launcher runs at a time; starting it again brings the open one forward.
- A copy run from Downloads (or anywhere outside its install) offers to install itself, and takes the game it already
  downloaded along.

### How the game is served

Nothing in this repo builds the game. The Arcana repo's deploy workflows compile the client
(`tools/build_client_release.ps1`), bake `Arcana-client.zip` and `client-metadata.json` into the server image, and the
server serves them at `/client/download` (resumable) and `/client/metadata`. The build id is the git tree of the client
source, so a server deploy that did not change the client costs players nothing. `GameLauncher/ClientMetadata.cs` is
this side of that contract.

### Live and Development

The two buttons under the logo pick the servers: **Live** (`app.tidansrealm.com`) or **Development**
(`devapp.tidansrealm.com`). Each has its own game folder (`Arcana\` and `Arcana-development\`) and its own build
check, and the launcher refuses a client marked for the other one. The launcher opens on the one picked last
time (Live the first time). Switching is off while an install, update or repair runs; a game already running keeps
running.

Development is behind an access code. The development server checks it (Arcana repo,
`Networking/ClientReleaseStore.cs`, header `X-Arcana-Access-Code`): without the right code it gives out no client.
The launcher asks for the code the first time Development is picked, or when the server stops taking the one it
has, and keeps it in `settings.json` encrypted for the Windows user (DPAPI), so the file opens nothing on another
PC or account. The code is sent only to that server's `/client/` routes. Cancel goes back to Live. The code itself
lives in the cluster (`arcana-client-access` Secret), never in this repo or the exe.

For one run, without changing the saved choice:

```powershell
& "$env:LOCALAPPDATA\Programs\Arcana Launcher\ArcanaLauncher.exe" --channel development
```

## Building

.NET 9 SDK, and Inno Setup 6.3 or newer for the installer.

```powershell
dotnet test GameLauncher.Tests
```

```powershell
dotnet publish GameLauncher/GameLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "/DSourceExe=$PWD\publish\ArcanaLauncher.exe" "/O$PWD\publish" installer\ArcanaLauncher.iss
```

A local build never updates itself from GitHub (only CI's `-p:ArcanaReleaseBuild=true` builds do). To try it against a
local server, set `ARCANA_LAUNCHER_SERVER_URL` (a game server) and `ARCANA_LAUNCHER_RELEASES_URL` (where releases come
from); release builds ignore both. A debug build also takes `--preview <state>` to show the window in one state with no
network.

## Releasing

Pushing to `main` (or running the `build-launcher` workflow) tests, publishes, builds the installer and creates release
`v1.0.<run>.0` marked Latest, with `ArcanaLauncherSetup.exe`, `ArcanaLauncher.exe` and `launcher-metadata.json`. It
also updates the old `latest` tag in place for launchers up to 1.0.9, which update from there.

Checklist for a release players will see:

1. CI green, and the release has all three files.
2. `https://tidangames.com/download/windows` hands out the new installer.
3. While unsigned: submit the new `ArcanaLauncherSetup.exe` and `ArcanaLauncher.exe` to Microsoft's file submission
   portal (as a software developer) so Defender learns them sooner.
4. Remove the `YamanoRealmsLauncher.exe` copy from the workflow once its download count on the `latest` tag stops
   rising.

## Reporting "the launcher won't open"

- Send `%LocalAppData%\Arcana Launcher\logs\launcher.log` (user names and profile folders are already replaced by
  `%USERNAME%` and the like).
- An antivirus may quarantine an unsigned file silently. Arcana Launcher says so when it finds `Arcana.exe` missing.
- Run the launcher from its install, not from Downloads: Windows loads some DLLs from the folder an exe runs in, and a
  stray one there (Lovely's `version.dll` did this) can crash it before any of its own code runs. The installer is safe
  to run from Downloads.
