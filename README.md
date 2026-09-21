# Arcana Launcher

Auto-updating Windows launcher for **Arcana**. It is the only way to get the game: it installs the native client, keeps it in step with the server, and starts it.

## Download

Grab the latest build:

➡ **[Download ArcanaLauncher.exe](https://github.com/jojobobby/Arcana-Game-Launcher/releases/latest/download/ArcanaLauncher.exe)**

Single self-contained file — no installer, no .NET runtime to install separately. Drop it in any folder; the launcher creates an `Arcana/` subfolder alongside itself the first time you hit Download.

Already have the old `YamanoRealmsLauncher.exe`? Open it and press **Update**: it replaces itself with this launcher.

## First-time setup (~10 seconds)

The launcher is a brand-new binary that Windows hasn't built up reputation for yet, so on **first launch only** you'll see:

> **Windows protected your PC**
> Microsoft Defender SmartScreen prevented an unrecognized app from starting.

To run it:

1. Click **More info** (small link, easy to miss)
2. Click **Run anyway**

You only have to do this once. After enough players download a given build, Windows trusts it automatically and the warning stops appearing for new players.

The launcher itself is the only file that triggers this — the actual game (`Arcana.exe`, which the launcher installs into a local folder) runs without any SmartScreen prompt.

## What it does

1. Asks the game server which client it was built with (`GET https://app.tidansrealm.com/client/metadata`)
2. Compares that **build id** with the one recorded in `Arcana/client-version.json`
3. If they differ, downloads `Arcana-client.zip` from the same server (`/client/download`), checks its SHA-256 against the metadata, unpacks it to a fresh folder and swaps it in — a failed or corrupt download never touches a working install
4. Hits Play → starts `Arcana/Arcana.exe`

The server's Docker image contains the client built from the same commit as the server itself, so the launcher always installs the client that matches the server you are about to play on.

The version in the corner — `Arcana v5.2.9 (8a991dfa1c24)` — is the game version plus the build id. The build id is the git tree of the client source, so two players who see the same id are running the same client, and a server update that did not change the client does not make anyone download it again.

The launcher also keeps itself current: it reads `launcher-metadata.json` from this repo's `latest` release and offers **Update Available** when its own SHA-256 differs.

### Development channel (testers)

A client is compiled for exactly one environment. To install and play the development build instead:

```powershell
.\ArcanaLauncher.exe --channel development
```

It talks to `devapp.tidansrealm.com`, installs into `Arcana-development/` beside the production install, and refuses any client whose metadata is not marked `development` (and the other way round).

## Verifying the download (optional, paranoid mode)

```powershell
Get-FileHash ArcanaLauncher.exe -Algorithm SHA256
```

Compare to the `sha256` field in `launcher-metadata.json` on the same GitHub release.

## Reporting "the launcher won't open"

If SmartScreen blocks it and the "Run anyway" path doesn't appear, check:

- Some corporate / school networks have policies that hide the "Run anyway" option entirely — try downloading on a personal machine
- Antivirus other than Defender may quarantine the file silently — add an exception for the folder you saved it in
- If neither, file an issue with the SmartScreen reason text shown in Defender

"The game files are in use" when updating or uninstalling means `Arcana.exe` is still running — close the game first.

## Building from source

Requires .NET 9 SDK + Visual Studio 2022 (for the WPF designer) or VS Build Tools.

```powershell
dotnet build GameLauncher.sln -c Release
dotnet test GameLauncher.Tests
```

The tests cover the whole update flow — metadata validation, verified download, atomic install/update/rollback, channel separation and self-update — against an in-process fake server, so they need no network.

Or to reproduce a CI-equivalent single-file build:

```powershell
dotnet publish GameLauncher/GameLauncher.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

Output lands at `publish/ArcanaLauncher.exe`.

## Where the client comes from

Nothing in this repo builds the game. The Arcana repo's deploy workflows compile the native Haxe client (`tools/build_client_release.ps1`), bake `Arcana-client.zip` + `client-metadata.json` into the server image, and the server serves them at `/client/download` and `/client/metadata`. The shape of that metadata is the contract between the two repos; `GameLauncher/ClientMetadata.cs` is this side of it.
