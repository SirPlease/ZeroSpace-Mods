# Block Auto Chase

Stop your units walking back into a fight after you have told them to go somewhere else.

## Read this first: only works when you host

Unit behavior is decided by whoever is hosting the match.  
When you host, this works for everyone in the match, including your teammate, who has no say
in it and cannot see that it is on.  
When you join someone else's match, their game decides how units behave and this pak does
nothing at all.  
There is no way around that from a pak, and there should not be one.  
A joining player's files must not change the host's simulation.

## What changes

Engage a fight, then right-click your units somewhere far away.  
They walk there, and then they turn around and walk all the way back to the fight on their
own.

That happens because a unit with no order and a live target walks into weapon range no matter
how far away that target is, with nothing checking the distance.  
This mod replaces that walk with "forget the target", so a unit you have sent somewhere drops
what it cannot reach and stays where you put it.

Melee units are left alone, because the same change would stop them ever closing on anything.

## Install

1. Close the game.
2. Copy `zzzz_BlockAutoChase_P.pak` into
   `...\steamapps\common\ZeroSpace\Zerospace\Content\Paks`
3. Start the game and host a match.

## Uninstall

Delete the file.

## Compatibility

- Built for game build **24827478**.  
  A game update that changes unit AI may need an updated pak.
- Conflicts with any other mod that replaces the unit behavior tree
  (`NovaCharacterBehaviorTree`).
- No settings, nothing to turn on.  
  It is either installed or it is not.

## Without the mod

Press `H` for hold position after you move a group.  
Holding has no move step at all, so a holding unit never walks off after anything.  
This works whether you host or join.

---

## Building it yourself

The patcher is C# on .NET 10 and uses [UAssetAPI](https://github.com/atenfyr/UAssetAPI), plus
`ZSPatchKit` from [`../lib`](../lib), so keep the two folders next to each other.

1. Pull `NovaCharacterBehaviorTree.uasset` (and `.uexp`) out of your own game install with
   [FModel](https://fmodel.app) or anything else built on CUE4Parse.  
   The game's paks are UE 4.27 and not encrypted, so there is no key to find.
2. Run the patcher, pointed at the folder holding them:

   ```powershell
   dotnet run --project patcher -- <folder with the original asset> <output folder>
   ```

3. Pack what it wrote with [repak](https://github.com/trumank/repak):

   ```powershell
   repak.exe pack --version V11 -m "../../../" <output folder> zzzz_BlockAutoChase_P.pak
   ```

The patch is three reference edits inside the behavior tree.  
Nothing is added to the asset and no code is rewritten.
