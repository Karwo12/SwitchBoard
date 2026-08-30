# SwitchBoard

Windows automation profile manager for launching applications, configuring processes and system settings, and restoring changes after a profile finishes.

[![CI](https://github.com/Karwo12/SwitchBoard/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Karwo12/SwitchBoard/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Karwo12/SwitchBoard?display_name=tag&sort=semver)](https://github.com/Karwo12/SwitchBoard/releases)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D4)](https://github.com/Karwo12/SwitchBoard)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)

## About

SwitchBoard lets you build automation profiles from an ordered list of actions. A profile can launch a program, change process parameters, select a power plan, change display or audio settings, wait for a process or window, evaluate a condition, and then keep selected changes available for restore.

The application interface is available in English and Polish. Profiles are organized into user-managed categories, and actions execute sequentially in their configured order.

## Key features

- Profile and category management, including rename, duplicate, delete, and reorder operations.
- Action Picker with search, four user-facing categories, and localized action names.
- Validation and no-op detection before a profile can run.
- Configurable retry attempts and failure handling per action.
- Restore of captured process, program, power-plan, display, audio, and device state where the selected action supports it.
- A single-action test command from the profile editor, with an undo option for pending changes.
- English and Polish localization.
- Built-in themes and editable custom themes, including custom background images and `.sbtheme` import/export.
- Profile JSON import/export.
- Conditional `Then` and `Else` branches with nested actions.
- Drag-and-drop and move-up/move-down ordering for categories, profiles, and actions.
- Activity and execution history with pending-restore and partial-restore status.

## Actions

The Action Picker currently exposes these actions:

### Programs and processes

- Run program
- Adjust process

### System and devices

- Windows service state
- Power plan
- Display settings
- Device state
- Audio settings

### Waiting and timing

- Wait for process
- Wait for process to exit
- Wait for window
- Delay

### Automation

- Run script
- Run another profile
- Condition
- Notification

## Restore

Actions that support restore can capture their previous state while a profile runs. The captured changes remain available after execution and can be restored from the application when requested. Program/process behavior and selected system settings can use action-specific restore behavior, including a restore script for scripts that change external state.

Restore is verified action by action and can be partial if Windows cannot restore one of the saved changes. It is not a transaction covering every possible Windows side effect.

## Download

Download the latest Windows build from [GitHub Releases](https://github.com/Karwo12/SwitchBoard/releases).

Each tagged release publishes a self-contained `win-x64` folder as a ZIP archive, so a separate .NET runtime installation is not required. Extract the archive and run `SwitchBoard.exe`.

MP4 backgrounds use the built-in WPF/Windows Media Player path by default. A release also creates the optional
`SwitchBoard-LibVLC-3.0.23.1-3.10.1-win-x64.zip` companion and its
`SwitchBoard-LibVLC-3.0.23.1-3.10.1-win-x64.sha256.txt` checksum. It is not part of the base app ZIP: SwitchBoard downloads,
verifies, and installs it only when the user chooses LibVLC in **Settings → Interface**. VLC Media Player itself is not required.

### For users

1. Open **Releases**.
2. Download the latest `SwitchBoard-vX.Y.Z-win-x64.zip`.
3. Extract the archive.
4. Run `SwitchBoard.exe`.

Release binaries are currently not code-signed, so Windows may display a SmartScreen warning.

## Building from source

Requirements:

- Windows
- .NET 10 SDK

The command-line SDK is sufficient; Visual Studio is not required.

```powershell
git clone https://github.com/Karwo12/SwitchBoard.git
cd SwitchBoard
dotnet restore .\SwitchBoard.sln
dotnet build .\SwitchBoard.sln
dotnet run --project .\SwitchBoard.csproj
```

## Tests

```powershell
dotnet test .\SwitchBoard.sln
```

The suite includes unit, view-model, and Windows integration tests. Some integration tests can be controlled skips when the runner does not expose a required audio endpoint, alternate display mode, administrator access, or another required Windows capability. These skips are environmental results, not test failures.

## User data

On Windows, SwitchBoard stores its local data under `%LOCALAPPDATA%\SwitchBoard`. This includes the catalog, settings, execution sessions, custom theme assets, and logs.

## Development and releases

Changes merged into `main` are checked by CI on `windows-latest` with the current .NET 10 SDK. A stable tag such as `v0.4.2` is the release version: the workflow uses `0.4.2` for the application build and creates `SwitchBoard-v0.4.2-win-x64.zip` together with its SHA-256 checksum. It also packages the isolated pinned LibVLC companion (`3.0.23.1` / LibVLCSharp `3.10.1`) and checksum; the main publish step explicitly fails if VLC files leak into the base package. Release notes are generated by GitHub.

The normal flow is: merge to `main`, let CI pass, then create and push a `vX.Y.Z` tag. The Release workflow validates that the tagged commit belongs to `main`, reruns restore/build/test, publishes the self-contained Windows package, and creates the GitHub Release.

Some Windows actions may require running SwitchBoard as administrator; the application itself does not always require administrator privileges.

This repository does not currently include a README-ready application screenshot. A current screenshot can be added later without changing the documentation structure.
