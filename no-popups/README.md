# No Popups

Block some annoying constantly appearing pop ups.

## What it stops

- **The Early Access welcome window**, which the game shows on every boot.  
  It is meant to show once, but the "you have seen this" flag never sticks.
- **The Galactic War preview window**, the one describing planned features, which opens
  the first time you enter Galactic War in a session.

You can still read either of them from the menus if you want to.

## Install

Download `zzzz_ZSNoPopups_P.pak` from this folder, or build it yourself from the source
next to it (see [Building it yourself](#building-it-yourself)).

1. Close the game.
2. Copy `zzzz_ZSNoPopups_P.pak` into
   `...\steamapps\common\ZeroSpace\Zerospace\Content\Paks`
3. Start the game.

## Uninstall

Delete the file.

## Compatibility

- Built for game build **24727905** (the 2026-08-15 patch).  
  A game update that changes the menu code may require an updated version of this pak.
- Conflicts with any other mod that replaces the same three widgets
  (`W_Menu_Frontend_ZS`, `ShipMenu`, `W_Menu_GalacticWar_Default`).
- Menus only, so it makes no difference to a match or to anyone you play with.
- Standalone.  
  It has no settings, so it does not need the mod manager or any other mod.

---

## Building it yourself

The patcher is C# on .NET 10 and uses [UAssetAPI](https://github.com/atenfyr/UAssetAPI).  
You need two things:

1. The three widgets above, `.uasset` and `.uexp`, **out of your own game install**.  
   Any CUE4Parse-based tool will do it, [FModel](https://fmodel.app) is the easy one.  
   The game's paks are UE 4.27 and not encrypted, so there is no key to find.  
   Put all three in one folder.
2. The patcher, pointed at that folder:

```powershell
dotnet run --project patcher -- <folder with the three .uasset files> <output folder>
```

Pack what it writes with [repak](https://github.com/trumank/repak):

```powershell
repak.exe pack --version V11 -m "../../../" <output folder> zzzz_ZSNoPopups_P.pak
```

The patcher checks that each statement it changes really is the jump it expects, so a game
update that moves this code fails the build instead of producing a broken pak.
