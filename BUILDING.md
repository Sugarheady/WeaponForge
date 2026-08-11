# Building Weapon Forge

You only need to tell the build where your copy of PUNK is. Everything else is already wired.

## Prerequisites

- **PUNK Playtest v0.12.9**, with **BepInEx** installed and the game **launched at least once**
  (BepInEx unpacks its `core` folder on first run).
- **Visual Studio 2022** (Community is fine) or just the **.NET Framework 4.7.2** targeting pack plus
  MSBuild.

## Why there's a setup step at all

This mod compiles against PUNK's own managed assemblies (`Punk.Main.dll`, the `UnityEngine.*`
modules, Sirenix, MyBox, UniTask…) and the BepInEx core. **Those DLLs are the game's property and are
deliberately not in this repo** — so the build has to find them in your own install.

Two folders are needed, both inside your PUNK directory:

| What | Where |
|---|---|
| Game assemblies | `<PUNK>\Punk_Data\Managed` |
| BepInEx core | `<PUNK>\BepInEx\core` |

`Directory.Build.props` at the repo root derives both from a single `PunkDir` property. **You never
edit a `.csproj`.**

## Pick one of these three

`<PUNK>` means the folder that *contains* `Punk_Data` and `BepInEx` — e.g.
`C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest`.

### 1. A local props file — easiest, and what most people want

Copy `Punk.props.example` to **`Punk.props`** in the repo root and edit the one line inside it.
`Punk.props` is git-ignored, so your path never lands in the repo.

Then just build normally — in Visual Studio, or:

```bash
msbuild WeaponForge.sln -p:Configuration=Debug
```

### 2. An environment variable

Set **`PUNK_DIR`** to your PUNK folder. Nothing to create, nothing to ignore. Good if you work on
both Forge mods, since they read the same variable.

### 3. On the command line

```bash
msbuild WeaponForge.sln -p:Configuration=Debug -p:PunkDir="C:\Games\PUNK Playtest"
```

> **Gotcha:** MSBuild splits the `-p:` switch on commas, so a path containing a comma
> (e.g. `D:\Games, Old\PUNK Playtest`) **fails to parse**, quoted or not. Use option 1 or 2 if your
> path has a comma in it.

If the build can't resolve the folder it stops with a message telling you what it looked for, rather
than burying you in a few hundred "type or namespace not found" errors.

## Output & deploying

The DLL lands in `WeaponForge\bin\Debug\WeaponForge.dll`. Copy it into
`<PUNK>\BepInEx\plugins\` (its own subfolder is fine) and launch the game.

On first run the mod creates `weapons`, `sprites` and `sounds` folders next to itself, with a
`README.txt` and example weapons.

> **There is no hot reload.** Weapon JSON is read once at startup, so every change needs a full game
> restart. A malformed file is skipped and the reason is logged — search `BepInEx\LogOutput.log` for
> `WeaponForge`.

## Notes

- `<PathMap>` is set in the `.csproj` on purpose: without it, the compiled DLL embeds the absolute
  path of whoever built it, which is then readable out of any released binary. Please leave it in.
- The `Directory.Build.props` fallback that points at a sibling `WhiteTeslaMod` folder is a leftover
  from the original dev machine. It only activates if that folder exists, so it's inert for you and
  can be ignored.
- Companion mod: **Module Forge** (https://github.com/Sugarheady/ModuleForge). The two cooperate at
  runtime but have **no build-time dependency** on each other — they find each other by reflection,
  so you can build and run either one alone.
