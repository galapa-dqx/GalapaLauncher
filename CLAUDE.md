# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GalapaLauncher is a launcher for the Dragon Quest X MMORPG built with Avalonia and C#. It supports Windows 10+ and Proton.

## Build Commands

```bash
dotnet restore                    # Install dependencies
dotnet build                      # Build solution
dotnet test                       # Run all tests
dotnet run --project Galapa.Launcher  # Run the launcher

# Release build
dotnet publish Galapa.Launcher --configuration Release -r win-x64 --self-contained false
```

## Solution Structure

- **Galapa.Launcher** (.NET 10.0): Main Avalonia desktop application for launching the game.
- **Galapa.Toolbox** (.NET 8.0): Secondary utility application for analyzing game data.
- **Galapa.Core** (.NET 8.0-windows): Game and authentication logic library.
- **Galapa.Launcher.Tests** / **Galapa.Core.Tests**: xUnit test projects
- **Galapa.TestUtilities**: Shared testing utilities
- **Talon.Injector** (.NET 10.0, x86): Starts DQX suspended and injects the native bootstrap.
- **Talon.Boot** (native C++, Win32): Hosts CoreCLR and owns the unpack-completion barrier.
- **Talon** (.NET 10.0, x86 process): Managed hook, VFS, and network runtime loaded inside DQX.
- **Talon.Tests**: Managed tests for the injector and in-process runtime.

Build `Talon.Boot` with MSBuild for `Win32` before building or publishing the
Injector. `dotnet build` cannot build the native project.

## Architecture

### Dependency Injection (DryIoc)
All services and ViewModels are registered in `Galapa.Launcher/Program.cs`. Use constructor injection.

### MVVM Pattern
- ViewModels inherit from `ObservableObject` (CommunityToolkit.Mvvm)
- Use `[ObservableProperty]` attribute for auto-generated properties
- Views are resolved via `ViewLocator.cs` using pattern matching (not reflection)

### Authentication Strategy Pattern
Login strategies in `Galapa.Core/Game/Authentication/`:
- `LoginStrategy` (abstract base)
- `SavedPlayerLoginStrategy`, `AutoLoginStrategy`, `GuestLoginStrategy`, `NewPlayerLoginStrategy`

### Player Data Model
Players sync across three data sources:
- `PlayerListJson` (our records in AppData)
- `PlayerListXml` (DQX's dqxPlayerList.xml)
- `IPlayerCredential` (Windows Credential Manager)

### Configuration
- `Settings.cs`: User settings (JSON in AppData)
- `Paths.cs`: Static path constants (%APPDATA%\GalapaLauncher)

## Key Conventions

- C# 14, nullable enabled, implicit usings
- Views: `.axaml` files in `Views/` folders grouped by feature (AppFrame, LoginFrame, SettingsFrame)
- ViewModels: Mirror view structure in `ViewModels/` folders
- Register new ViewModels in `Program.cs` and add case to `ViewLocator.cs`
