// No Popups
//
// Stops the two windows that open by themselves in the menus: the Early Access welcome,
// and the Galactic War preview.

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using ZSPatchKit;
using static ZSPatchKit.Kis;

class Program
{
    const string DefaultInput = @"mods\nopopups\raw";
    const string DefaultOutput = @"mods\nopopups\pak_build";

    static ModAsset A = null!;
    static UAsset asset => A.Asset;

    static void Main(string[] args)
    {
        var root = Repo.Root(DefaultInput);
        var inDir = args.Length > 0 ? args[0] : Path.Combine(root, DefaultInput);
        var outRoot = args.Length > 1 ? args[1] : Path.Combine(root, DefaultOutput);

        // Each window is stopped from being asked for, not closed after it opens: closing
        // one from code loses camera control for the session. Offsets are from build
        // 24727905, and each one is checked before it is changed.

        // 1. Early Access welcome. The menu graph reads "has the player seen it", and
        //    on "no" falls through to the show block. Send that jump to the skip path.
        Patch(inDir, outRoot, "W_Menu_Frontend_ZS", "ExecuteUbergraph_W_Menu_Frontend_ZS",
            @"Zerospace\Content\UserInterface\Widgets\Frontend",
            pivot: 3088, show: 210, skip: 300);

        // 2. Galactic War preview, opened from the ship menu. The vanilla skip path ends
        //    the current flow instead of jumping, so the show jump is replaced in place
        //    by that same end-of-flow instruction plus padding to keep the size.
        Patch(inDir, outRoot, "ShipMenu", "ExecuteUbergraph_ShipMenu",
            @"Zerospace\Content\RTSGameSample\UI\MainMenu",
            pivot: 2759, show: 125, skip: null);

        // 3. The preview also opens the first time you enter Galactic War in a session.
        //    Here the test itself branches to the show block, so the branch goes to the
        //    end of the graph instead.
        Patch(inDir, outRoot, "W_Menu_GalacticWar_Default", "ExecuteUbergraph_W_Menu_GalacticWar_Default",
            @"Zerospace\Content\UserInterface\Widgets\Frontend\GalacticWar",
            pivot: 745, show: 10, skip: 2694, pivotIsBranch: true);

        Console.WriteLine("OK");
    }

    // Repo root and Measure: mods\lib\ZSPatchKit.


    // pivot: the jump that leads to the popup. show: where it goes now, checked.
    // skip:  where it should go, or null for "end this flow", as vanilla's skip path does.
    static void Patch(string inDir, string outRoot, string assetName, string ubergraph, string relPath,
        uint pivot, uint show, uint? skip, bool pivotIsBranch = false)
    {
        Console.WriteLine($"=== {assetName} ===");
        A = ModAsset.Load(Path.Combine(inDir, assetName + ".uasset"));
        if (!asset.VerifyBinaryEquality()) throw new Exception($"{assetName}: round-trip not binary-equal");
        KismetSerializer.asset = asset;

        var f = asset.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == ubergraph);
        var code = f.ScriptBytecode.ToList();

        int pivotIdx = -1;
        uint off = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (off == pivot) { pivotIdx = i; break; }
            off += (uint)Measure(code[i]);
        }
        if (pivotIdx < 0) throw new Exception($"{assetName}: nothing starts at offset {pivot}");

        int sizeBefore = Measure(code[pivotIdx]);
        string what;

        if (pivotIsBranch)
        {
            if (!(code[pivotIdx] is EX_JumpIfNot branch) || branch.CodeOffset != show)
                throw new Exception($"{assetName}: statement at {pivot} is not a branch to {show} - the game changed");
            branch.CodeOffset = skip!.Value;
            what = $"branch {show} -> {skip}";
        }
        else if (skip.HasValue)
        {
            if (!(code[pivotIdx] is EX_Jump jump) || jump.CodeOffset != show)
                throw new Exception($"{assetName}: statement at {pivot} is not a jump to {show} - the game changed");
            jump.CodeOffset = skip.Value;
            what = $"jump {show} -> {skip}";
        }
        else
        {
            if (!(code[pivotIdx] is EX_Jump jump2) || jump2.CodeOffset != show)
                throw new Exception($"{assetName}: statement at {pivot} is not a jump to {show} - the game changed");
            // end the flow instead of jumping, and pad out to the old size so nothing
            // after this point moves. The padding is never reached.
            var replacement = new List<KismetExpression> { new EX_PopExecutionFlow() };
            while (replacement.Sum(Measure) < sizeBefore) replacement.Add(new EX_Nothing());
            if (replacement.Sum(Measure) != sizeBefore)
                throw new Exception($"{assetName}: cannot pad the replacement to {sizeBefore} bytes");
            code.RemoveAt(pivotIdx);
            code.InsertRange(pivotIdx, replacement);
            what = $"jump {show} -> end of flow (+{replacement.Count - 1} padding)";
        }

        f.ScriptBytecode = code.ToArray();
        CheckJumps(f, assetName);

        var outDir = Path.Combine(outRoot, relPath);
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, assetName + ".uasset");
        asset.Write(outPath);

        // read the written file back with a fresh parser and check it again
        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        KismetSerializer.asset = check;
        CheckJumps(check.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == ubergraph), assetName + " (reloaded)");
        Console.WriteLine($"  {what}; every jump lands on a statement");
    }

    // Every jump has to point at the start of a statement. A jump into the middle of
    // one makes the VM read whatever bytes are there as an instruction.
    static void CheckJumps(FunctionExport f, string where)
    {
        var starts = new HashSet<uint>();
        uint off = 0;
        foreach (var e in f.ScriptBytecode) { starts.Add(off); off += (uint)Measure(e); }
        foreach (var e in f.ScriptBytecode)
        {
            uint? target = e switch { EX_Jump j => j.CodeOffset, EX_JumpIfNot jn => jn.CodeOffset, _ => null };
            if (target.HasValue && !starts.Contains(target.Value))
                throw new Exception($"{where}: jump to {target.Value} lands nowhere");
        }
    }
}
