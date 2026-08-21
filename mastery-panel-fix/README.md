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

## What changes on the match end screen

- The level line reads `MASTERY LVL 2/4 [+5 980 XP]`: the real level out of four, and the
  mastery XP the match paid.  
  The game shows a number that counts stacks, not levels, which is why it can read 3 when the
  planet is level 2.
- The mastery bar is filled from the planet's real XP inside that level, instead of from a
  window the game looks up with the wrong number.
- The rows show what the planet gives at each of its four levels, lit for the ones you have
  and dim for the rest.  
  The game repeats the same per-stack line instead, and only has room for three, so the mod
  adds the fourth row.

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

- Built for game build **24827478** (the 2026-08-20 patch).  
  A game update that changes the planet panel may require an updated version of this pak.
- Conflicts with any other mod that replaces the planet panel (`StarSystemWidgetV2`) or the
  match end screen (`WBP_MMOEndScreen`).
- Works whether you host or join.  
  Nothing is sent to the server, and it makes no difference to anyone else in your party.
- Standalone.  
  It has no settings, so it does not need the mod manager or any other mod.

---

## Building it yourself

The patcher is C# on .NET 10 and uses [UAssetAPI](https://github.com/atenfyr/UAssetAPI) to
edit the widget's compiled Blueprint bytecode.  
You need two things:

1. `StarSystemWidgetV2.uasset` and `WBP_MMOEndScreen.uasset` (with their `.uexp` files)
   **out of your own game install**.  
   Any CUE4Parse-based tool will do it, [FModel](https://fmodel.app) is the easy one.  
   The game's paks are UE 4.27 and not encrypted, so there is no key to find.
2. The patcher, pointed at both:

```powershell
dotnet run --project patcher -- <StarSystemWidgetV2.uasset> <output folder> <WBP_MMOEndScreen.uasset>
```

It writes both widgets under `<output folder>\Zerospace\Content\`.  
Pack that with [repak](https://github.com/trumank/repak):

```powershell
repak.exe pack --version V11 -m "../../../" <output folder> zzzz_MasteryPanelFix_P.pak
```

The patcher finds the places to edit by the shape of the code rather than by hard-coded
addresses, so it usually still works after a game update.