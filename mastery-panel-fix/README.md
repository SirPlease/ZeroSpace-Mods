# Mastery Panel Fix

The planet panel reports mastery progress wrong by the current game logic, this mod fixes
that.  
This is a purely "cosmetic" mod and prevents confusion for players.

## What changes on a planet's panel

- The Mastery Level and EXP required has been corrected.
- MASTERED appears when the planet is actually mastered (72,000 XP).
- Each level row shows what you hold at that level instead of repeating the same per-stack
  line four times (which is also incorrect).
- Rows you have unlocked are lit correctly, including on mastered planets.

One thing to keep in mind about the level rows: they show what a planet **gives** you.  
At least two of the ten masteries do not apply every stack you own once you are in a match,
so treat the rows as what you are owed rather than a promise about a given battle.

## Install

Download `zzzz_MasteryPanelFix_P.pak` from this folder, or build it yourself from the
source next to it (see [Building it yourself](#building-it-yourself)).

1. Close the game.
2. Copy `zzzz_MasteryPanelFix_P.pak` into
   `...\steamapps\common\ZeroSpace\Zerospace\Content\Paks`
3. Start the game and open any planet in Galactic War.

## Uninstall

Delete the file.

## Compatibility

- Built for game build **24727905** (the 2026-08-15 patch).  
  A game update that changes the planet panel may require an updated version of this pak.
- Conflicts with any other mod that replaces the same planet-panel widget
  (`StarSystemWidgetV2`).
- Works whether you host or join.  
  Nothing is sent to the server, and it makes no difference to anyone else in your party.
- Standalone.  
  It has no settings, so it does not need the mod manager or any other mod.

---

## Building it yourself

The patcher is C# on .NET 10 and uses [UAssetAPI](https://github.com/atenfyr/UAssetAPI) to
edit the widget's compiled Blueprint bytecode.  
You need two things:

1. `StarSystemWidgetV2.uasset` (and `.uexp`) **out of your own game install**.  
   Any CUE4Parse-based tool will do it, [FModel](https://fmodel.app) is the easy one.  
   The game's paks are UE 4.27 and not encrypted, so there is no key to find.
2. The patcher, pointed at that file:

```powershell
dotnet run --project patcher -- <original StarSystemWidgetV2.uasset> <output folder>
```

It writes `<output folder>\Zerospace\Content\RTSGameSample\UI\MainMenu\StarSystemDetails\`.  
Pack that with [repak](https://github.com/trumank/repak):

```powershell
repak.exe pack --version V11 -m "../../../" <output folder> zzzz_MasteryPanelFix_P.pak
```

The patcher finds the places to edit by the shape of the code rather than by hard-coded
addresses, so it usually still works after a game update.