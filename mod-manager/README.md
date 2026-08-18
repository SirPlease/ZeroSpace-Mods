# ZS Mod Manager

Adds a **Mods** page to the game's Settings screen, where mods that have settings can be
turned on and off and configured while the game is running.

On its own it does nothing visible.  
It is the page other mods put their settings on, so install it alongside a mod that asks for
it.

## What it adds

- A Mods tab in Settings, next to the game's own tabs.
- One section per installed mod that has settings, with the mod's name as the heading.
- Each mod's first setting is an on/off switch for the whole mod.  
  Turning it off grays out that mod's other settings.
- Your choices are saved and come back next time you play.
- The game's settings search finds mod settings too.

## Install

Download `zzzz_0_ZSModManager_P.pak` from this folder, or build it yourself from the source
next to it (see [Building it yourself](#building-it-yourself)).

1. Close the game.
2. Copy `zzzz_0_ZSModManager_P.pak` into
   `...\steamapps\common\ZeroSpace\Zerospace\Content\Paks`
3. Start the game and open Settings.  
   There is a Mods tab at the end.

Keep the file name as it is.  
The `0` makes it load before other mods, which is what lets them register.

## Uninstall

Delete the file.  
Mods that used it keep working, on their default settings.

## Compatibility

- Built for game build **24727905** (the 2026-08-15 patch).  
  A game update that changes the Settings screen may require an updated version of this pak.
- Replaces the two Settings container widgets (`W_OptionsMenu`, `W_OptionsMenu_ZS`), so it
  conflicts with any other mod that replaces those.
- Settings only.  
  Nothing is sent to the server and nothing changes for other players.
- A mod section only appears if that mod is installed and this manager knows its name.  
  See below.

---

## Adding your own mod to the page

The manager finds a mod through an asset named after it, `/Game/Mods/Registry/<YourModId>`,
which ships inside your own pak.  
That asset describes your settings, and the generator here writes it for you.

1. Write a manifest.  
   `example/ExampleMod.json` is a working one:

```json
{
  "id": "ExampleMod",
  "name": "Example Mod",
  "pakBuild": "mods/examplemod/pak_build",
  "settings": [
    { "key": "Enabled", "label": "Enabled", "master": true, "default": true,
      "description": "Turn the mod on or off." },
    { "key": "Size", "label": "Size", "type": "dropdown",
      "options": ["Small", "Medium", "Large"], "defaultOption": "Medium",
      "description": "How big they are drawn." }
  ]
}
```

- `id` is used for the asset name and for the setting keys, so keep it short and unique.
- The first setting must be the on/off switch: `"master": true` and type toggle.
- `type` is `toggle` or `dropdown`.  
  All of a mod's dropdowns share one option list, given once with `options` or in a file
  named by `optionsFile`.
- `pakBuild` is where your mod's pak tree lives.  
  The registration asset is written there, ready to pack with the rest of your mod.

2. Run the generator with your manifest folder.  
   It writes your registration asset and a manager pak that knows about it.

3. Read the values at run time.  
   They are stored as text in a map called `SettingsMap` on a save game in the slot
   `ZSModSettings`, under the key `<id>_<key>`: `"1"` or `"0"` for a toggle, and the chosen
   index for a dropdown.  
   If there is no save file yet, your mod should use the defaults from your manifest.

A mod the manager has never heard of cannot appear on the page, because the page has to name
what it looks for.  
So the manager needs regenerating when a new mod wants a section.

## Building it yourself

The patcher is C# on .NET 10 and uses [UAssetAPI](https://github.com/atenfyr/UAssetAPI) to
edit the game's compiled Blueprint bytecode.  
You need:

1. Four widgets **out of your own game install**, in one folder: `W_OptionsMenu`,
   `W_OptionsMenu_ZS`, `W_SettingsMenu_General` and `BP_ListItemObj_MapInfo`, each as
   `.uasset` and `.uexp`.  
   Any CUE4Parse-based tool will do it, [FModel](https://fmodel.app) is the easy one.  
   The game's paks are UE 4.27 and not encrypted, so there is no key to find.
2. A folder of manifests, one json per mod.

```powershell
dotnet run --project patcher -- <manifests> <original widgets> <output folder>
```

Pack what it writes with [repak](https://github.com/trumank/repak):

```powershell
repak.exe pack --version V11 -m "../../../" <output folder> zzzz_0_ZSModManager_P.pak
```

## How the page is built

The Mods page is a copy of the game's own General settings page, emptied out.  
The four sections that page already has are reused, and more are cloned at build time when
there are more mods, so the number of mods is not capped.

Nothing about a mod is baked into the page.  
Opening it checks which registration assets are actually present, reads each one's
description of itself, and creates the rows there and then, using the same widgets and the
same wiring the game's own settings pages use.  
A mod that is not installed fails its import quietly and its section stays hidden, so any
mix of installed mods works.
