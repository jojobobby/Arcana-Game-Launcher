# Yamano Realms Launcher

Auto-updating Windows launcher for **Yamano Realms** (no-wipe beta).

## Download

Grab the latest build:

➡ **[Download YamanoRealmsLauncher.exe](https://github.com/jojobobby/Arcana-Game-Launcher/releases/latest/download/YamanoRealmsLauncher.exe)**

Single self-contained file — no installer, no .NET runtime to install separately. Drop it in any folder; the launcher will create a `YamanoRealms/` subfolder alongside itself the first time you hit Play.

## First-time setup (~10 seconds)

The launcher is a brand-new binary that Windows hasn't built up reputation for yet, so on **first launch only** you'll see:

> **Windows protected your PC**
> Microsoft Defender SmartScreen prevented an unrecognized app from starting.

To run it:

1. Click **More info** (small link, easy to miss)
2. Click **Run anyway**

You only have to do this once. After enough players download a given build, Windows trusts it automatically and the warning stops appearing for new players.

The launcher itself is the only file that triggers this — the actual game (the Adobe AIR captive runtime + SWF that the launcher installs into a local folder) runs without any SmartScreen prompt.

## What it does

1. Checks the latest client release on the [game repo](https://github.com/jojobobby/Arcana/releases/latest)
2. Compares the remote SWF MD5 hash to whatever you have installed locally
3. If different, downloads the Adobe AIR captive runtime bundle (`YamanoRealms-AIR.zip`) and extracts it next to itself
4. Hits Play → spawns the bundled `YamanoRealms.exe`

The version string you see in the corner — `YamanoRealms-no-wipe-betatesting-(hash)` — is the first 6 hex characters of the SWF's MD5, so two players who see the same hash are running the exact same build.

## Verifying the download (optional, paranoid mode)

```powershell
Get-FileHash YamanoRealmsLauncher.exe -Algorithm SHA256
```

Compare to the `sha256` field in `launcher-metadata.json` on the same GitHub release.

## Reporting "the launcher won't open"

If SmartScreen blocks it and the "Run anyway" path doesn't appear, check:

- Some corporate / school networks have policies that hide the "Run anyway" option entirely — try downloading on a personal machine
- Antivirus other than Defender may quarantine the file silently — add an exception for the folder you saved it in
- If neither, file an issue with the SmartScreen reason text shown in Defender

## Building from source

Requires .NET 9 SDK + Visual Studio 2022 (for the WPF designer) or VS Build Tools.

```powershell
dotnet build GameLauncher.sln -c Release
```

Or to reproduce a CI-equivalent single-file build:

```powershell
dotnet publish GameLauncher/GameLauncher.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

Output lands at `publish/YamanoRealmsLauncher.exe`.
