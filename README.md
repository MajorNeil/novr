# [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/) VR

NOVR is a reworked version of [UUVR](https://github.com/Raicuparta/uuvr) designed and optimized for Nuclear Option.

This is a fork of [InfernoSuperNova/novr](https://github.com/InfernoSuperNova/novr). It installs into the same folders, and its installer can replace or remove a copy of NOVR installed by the original installer.

## User Installation

### Recommended: GUI installer

1. Close Nuclear Option before installing or updating the mod.
2. Download the latest installer from the [releases page](https://github.com/MajorNeil/novr/releases/latest), or build it yourself (see [Installer development](#installer-development)):
    - **Windows:** `NOVR.Installer-Win.exe`
    - **Linux/Proton:** `NOVR.Installer-Linux`
3. Run the installer.
    - On Linux, you may need to make it executable first: `chmod +x NOVR.Installer-Linux`.
4. If Nuclear Option is not found automatically, choose the game folder manually.
    - The selected folder must contain `NuclearOption_Data/Managed`.
5. Click **Install**.
6. Launch Nuclear Option from Steam.

The installer can also update, repair, or uninstall NOVR after it is installed.

If NOVR is already installed by the original installer, it is detected and the installer offers **Replace with this build** (installs this fork in its place) and **Uninstall NOVR** (removes it). Your NOVR settings in `BepInEx/config` are kept either way.

### What the installer does

The installer:

- Finds your Nuclear Option install.
- Installs BepInEx 5.x if it is missing.
- Uses the NOVR build bundled into the installer, or downloads the latest release zip from this fork if that is newer.
- Installs NOVR into:
    - `BepInEx/plugins/NOVR`
    - `BepInEx/patchers/NOVR`
- Writes the installed NOVR version to `BepInEx/plugins/NOVR/version.txt`, plus a `source=MajorNeil/novr` line so a build from this fork can be told apart from one installed by the original installer.
- On Linux/Proton, attempts to configure the `winhttp` Wine override needed by BepInEx.

Use BepInEx 5.x only. Do not use BepInEx 6.x unless the project explicitly says it is supported.

### Manual zip install

Use this only if the installer does not work for your setup.

1. Close Nuclear Option.
2. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases/latest) into the Nuclear Option game folder.
    - After installing BepInEx, this folder should exist: `Nuclear Option/BepInEx/core`.
3. Download `NOVR.zip` from the [latest NOVR release](https://github.com/InfernoSuperNova/novr/releases/latest).
4. Extract the contents of `NOVR.zip` into `Nuclear Option/BepInEx`.
5. Confirm these files exist:

    ```text
    Nuclear Option/BepInEx/plugins/NOVR/NOVR.dll
    Nuclear Option/BepInEx/patchers/NOVR/NOVR.Patcher.dll
    ```

6. Launch Nuclear Option from Steam.

`NOVR.zip` contains `plugins` and `patchers` folders. Extract it into the existing `BepInEx` folder, not directly into the game root.

### First launch behavior

On game startup, the NOVR BepInEx patcher copies required XR support files into `NuclearOption_Data`. These files may be overwritten every time the game starts.

If the game is already running while installing or rebuilding, Windows may prevent those files from being replaced. Close Nuclear Option before installing, updating, or building the mod. Files that are already up to date are skipped, and a file that can't be replaced is logged and retried on the next launch instead of stopping the rest of the copy.

### In-game VR UI controls

While an in-game menu (pause menu, map, settings, key bindings) has the cursor up, a small **VR UI SETTINGS** button sits below the menus. It opens a panel with:

- **MENUS**: **SIZE -/+** and **CLOSER/FARTHER** for the in-game menus, map and key binding screen.
- **HUD**: **SIZE -/+** for the flight HUD (speed, altitude, heading and artificial horizon readouts, pitch ladder, weapon panels and target markers). Markers keep pointing at what they mark; only how big they're drawn changes.
- **VR CENTER** (recenters after 2 seconds, so look where forward should be) and **CLOSE**.

The values are saved in the `In-Game UI` section of `BepInEx/config/deltawing.novr.cfg`. NOVR's own main menu keeps its separate **VR UI SETTINGS** page.

### Linux/Proton notes

The installer tries to set the required `winhttp` override automatically. If BepInEx does not load under Proton, configure the game's Wine prefix manually so `winhttp` uses `native,builtin`.

See the [BepInEx running under Proton guide](https://docs.bepinex.dev/articles/advanced/proton_wine.html) for the manual setup.

## Building

These steps are for developers building NOVR from source.

1. Install a .NET/MSBuild toolchain that can build SDK-style .NET Framework 4.8 projects.
    - **Windows:** Visual Studio 2022 or Build Tools for Visual Studio 2022 with the **.NET Framework 4.8 targeting pack** installed.
    - **Linux:** the .NET SDK plus Mono/MSBuild and .NET Framework reference assemblies. Distro package names vary, but you usually want packages such as `dotnet-sdk`, `mono`, `msbuild`/`mono-msbuild`, and `mono-reference-assemblies`/`.NET Framework 4.8 reference assemblies`.
2. Have Nuclear Option installed at:
    - **Linux:** `~/.steam/steam/steamapps/common/Nuclear Option` OR `~/.steam/debian-installation/steamapps/common/Nuclear Option` OR `~/.local/share/Steam/steamapps/common/Nuclear Option`
    - **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option` OR `C:\Program Files\Steam\steamapps\common\Nuclear Option` OR `D:\SteamLibrary\steamapps\common\Nuclear Option`

    If none of these options work for you, set the `NUCLEAR_OPTION_GAME_DIR` environment variable or pass `/p:NuclearOptionGameDir="path\to\Nuclear Option"` when building.
3. Ensure BepInEx 5.x is installed inside the Nuclear Option directory.
4. Build the `Release` configuration from your IDE of choice. JetBrains Rider is tested; Visual Studio should work too. To build from the command line, run this from the project root:

    ```bash
    dotnet build NuclearOptionVirtualRealityMod.sln -c Release
    ```

    The build output is written under `build-output`, as well as copied directly to the BepInEx directory.

## Installer development

The GUI installer is built with Avalonia. Building it in `Release` automatically publishes single-file launchers for Linux and Windows into `dist/`:

```bash
dotnet build NOVR.Installer/NOVR.Installer.csproj -c Release
```

Outputs:

- `dist/NOVR.Installer-Linux`
- `dist/NOVR.Installer-Win.exe`

Building the full solution in `Release` also creates `dist/NOVR.zip`, ready to upload as the mod release asset. The solution builds `NOVR.zip` before the installers and embeds it into them, so the installers in `dist/` can install that build without any GitHub release (pass `-p:BundleNovrPackage=false` for installers that only download). The bundled version comes from the `BepInPlugin` version in `NOVR/NOVRPlugin.cs`.

To publish a release for the installer's update check, create a release on this repository whose tag is the plugin version (for example `0.4.5`) and upload `dist/NOVR.zip` and the two installers to it.

## License

    Nuclear Option VR Mod
    Copyright (C) 2026 InfernoSuperNova (DeltaWing)

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.

# Original README below

> # Universal Unity VR
>
> [![Raicuparta's VR mods](https://raicuparta.com/img/badge.svg)](https://raicuparta.com)
>
> Use [Rai Pal](https://pal.raicuparta.com) to install this mod.
>
> ## License
>
>     Rai Pal
>     Copyright (C) 2024  Raicuparta
>
>     This program is free software: you can redistribute it and/or modify
>     it under the terms of the GNU General Public License as published by
>     the Free Software Foundation, either version 3 of the License, or
>     (at your option) any later version.
>
>     This program is distributed in the hope that it will be useful,
>     but WITHOUT ANY WARRANTY; without even the implied warranty of
>     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
>     GNU General Public License for more details.
>
>     You should have received a copy of the GNU General Public License
>     along with this program.  If not, see <https://www.gnu.org/licenses/>.
