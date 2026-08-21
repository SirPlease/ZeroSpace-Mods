# ZeroSpace Mods

Small client-side mods for **ZeroSpace** (Starlance Studios), mostly around Galactic War.  
Each one is a patch pak: it overrides one widget from the game and nothing else.  
No game file is changed, and nothing is added to your install except the pak itself.

| Mod | What it does | Mod Manager |
|---|---|---|
| [Mastery Panel Fix](mastery-panel-fix/) | Fixes the mastery level, XP and level rows shown on a planet's panel | ❌ |
| [Mastered Marker](mastered-marker/) | Shows a planet's mastery rank, and marks the mastered ones, on the galaxy map's hover tooltip | ❌ |
| [No Popups](no-popups/) | Stops the Early Access welcome and the Galactic War preview from opening by themselves | ❌ |
| [Team Colors](team-colors/) | Colors players by who they are to you: yourself, your teammate, mission allies, enemies | ✅ |

- ❌ = Nothing to set up.  
  Install the pak and you are done.
- ✅ = Has settings you can change in game, under Settings -> Mods.  
  That page comes from the ZS Mod Manager, below.  
  Without it the mod still works, it just uses its default settings.

## [ZS Mod Manager](mod-manager/)

Adds a Mods page to the game's Settings screen.  
Install it when you want to be able to configure a Mod Manager using mod in-game.  
Mods that support the Mod Manager can run fine without the Mod Manager, but you won't be able to customize the settings in-game.

## Installing

Every mod folder has three things in it: the pak you can install right away, the source
that built it, and a README that covers both.

1. Close the game.
2. Copy the mod's `zzzz_*_P.pak` into
   `…\steamapps\common\ZeroSpace\Zerospace\Content\Paks`
3. Done!

## Uninstalling

To remove a mod, delete its pak.  
That's it!

## Building it yourself

The pak in each folder can be rebuilt from the source next to it.  
The one thing not in this repo is the game's own files: the patcher needs the original
widget as input, and you take that from your own copy of the game.

1. Pull the `.uasset` (and `.uexp`) named in the mod's README out of your ZeroSpace install
   with [FModel](https://fmodel.app) or anything else built on CUE4Parse.  
   The game's paks are UE 4.27 and not encrypted, so there is no key to find.
2. Run the patcher (.NET 10):

   ```powershell
   dotnet run --project patcher -- <original .uasset> <output folder>
   ```

3. Pack what it wrote with [repak](https://github.com/trumank/repak):

   ```powershell
   repak.exe pack --version V11 -m "../../../" <output folder> zzzz_<Mod>_P.pak
   ```

The newer patchers share a small library, [`lib/ZSPatchKit`](lib/ZSPatchKit).  
Keep it where it is and they will find it.

## How the mods work

They edit the compiled Blueprint bytecode with
[UAssetAPI](https://github.com/atenfyr/UAssetAPI) instead of replacing whole Blueprints.  
Each edit is written to be the same size as the code it replaces: the old statement becomes
a jump plus padding, and the new code sits in a block added after the end of the function,
which jumps back when it is done.  
That way nothing else in the widget shifts around, and every part of the asset we did not
mean to touch is left exactly as the game shipped it.  
Each build is then checked by reading the finished pak back with a second parser and
comparing it against the original.

## Notes

- These only change what you see on your own screen.      
- A game update can break a pak until it is rebuilt.
- Unofficial, and not connected to Starlance Studios.
