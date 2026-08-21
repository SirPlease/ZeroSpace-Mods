// Block Auto Chase
//
// Stops a unit walking back to the fight after you have right-clicked it somewhere else.
//
// `[122]` is the orderless ranged engage sequence, and its child 0 is gated on "target is
// beyond leash range" but holds the chase. This points that child at a ClearTarget task
// instead, so the same gate drops the target rather than walking to it. The task is `[642]`,
// lifted out of the ORDER JUNGLE 2 branch, which nothing issues. `[747]`, an early stop
// order inside the move parallel, is unlinked as well: it aborts the move sequence before
// SetHomeLocation runs and leaves a stale home to walk back to.
//
// Three reference edits inside existing exports. No export, import or name is added, and
// the preload dependency lists are maintained by hand to match the cooker convention,
// because the loader asserts on those.

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using ZSPatchKit;

class Program
{
    const string DefaultInput  = @"mods\blockautochase\raw";
    const string DefaultOutput = @"mods\blockautochase\pak_build";
    const string AssetSubDir   = @"Zerospace\Content\Nova\NovaAI";
    const string AssetName     = "NovaCharacterBehaviorTree";

    // Export indices, 0-based, the same numbering `tools\bt_dump.py` prints in brackets.
    const int DonorClearTarget = 642;   // NovaBTTask_ClearTarget
    const int DonorParent      = 206;   // BTComposite_Sequence, ORDER JUNGLE 2
    const int ChaseParent      = 122;   // BTComposite_Sequence, orderless ranged engage
    const int ChaseSubtree     = 87;    // BTComposite_Selector, holds the chase move task
    const int ChaseGateForce   = 290;   // BTDecorator_ForceSuccess on that child
    const int ChaseGateRange   = 500;   // NovaBTDecorator_IsInRange, leash, inverted
    const int MoveBackground   = 61;    // BTComposite_Selector, background of the move parallel
    const int EarlyStopOrder   = 747;   // NovaBTTask_IssueStopOrder reached from [61]

    static int Main(string[] args)
    {
        var root    = Repo.Root(Path.Combine(DefaultInput, AssetName + ".uasset"));
        var inDir   = args.Length > 0 ? args[0] : Path.Combine(root, DefaultInput);
        var outRoot = args.Length > 1 ? args[1] : Path.Combine(root, DefaultOutput);

        var src = Path.Combine(inDir, AssetName + ".uasset");
        if (!File.Exists(src)) throw new FileNotFoundException("vanilla asset missing", src);

        var A = ModAsset.Load(src);
        var asset = A.Asset;
        if (!asset.VerifyBinaryEquality())
            throw new Exception("vanilla asset does not round-trip - refusing to patch");
        int exportsBefore = asset.Exports.Count;
        int importsBefore = asset.Imports.Count;
        int namesBefore   = asset.GetNameMapIndexList().Count;

        // ---- guards: the tree must be the one this patch was written against ----
        Expect(asset, DonorClearTarget, "NovaBTTask_ClearTarget");
        Expect(asset, DonorParent,      "BTComposite_Sequence");
        Expect(asset, ChaseParent,      "BTComposite_Sequence");
        Expect(asset, ChaseSubtree,     "BTComposite_Selector");
        Expect(asset, ChaseGateForce,   "BTDecorator_ForceSuccess");
        Expect(asset, ChaseGateRange,   "NovaBTDecorator_IsInRange");
        Expect(asset, MoveBackground,   "BTComposite_Selector");
        Expect(asset, EarlyStopOrder,   "NovaBTTask_IssueStopOrder");

        var donorKids = Children(asset, DonorParent);
        if (donorKids.Count != 2 || ChildTaskOf(donorKids[0]) != Pkg(DonorClearTarget).Index)
            throw new Exception($"[{DonorParent}] is not the expected 2-child sequence starting with the donor");
        var chaseKids = Children(asset, ChaseParent);
        if (chaseKids.Count != 2 || ChildCompositeOf(chaseKids[0]) != Pkg(ChaseSubtree).Index)
            throw new Exception($"[{ChaseParent}] child 0 is not the expected chase subtree");
        var gate = DecoratorsOf(chaseKids[0]).Select(d => d.Index - 1).ToArray();
        if (gate.Length != 2 || gate[0] != ChaseGateForce || gate[1] != ChaseGateRange)
            throw new Exception($"[{ChaseParent}] child 0 is gated by [{string.Join("], [", gate)}], "
                              + $"expected [{ChaseGateForce}] and [{ChaseGateRange}] - the leash gate moved");
        var bgKids = Children(asset, MoveBackground);
        if (bgKids.Count != 2 || ChildTaskOf(bgKids[0]) != Pkg(EarlyStopOrder).Index)
            throw new Exception($"[{MoveBackground}] child 0 is not the expected early IssueStopOrder");

        // ---- 1. lift the donor entry out of its sequence ----
        // Its decorators would be orphaned rather than moved, so there must not be any.
        var donorEntry = (StructPropertyData)donorKids[0];
        if (DecoratorsOf(donorEntry).Length != 0)
            throw new Exception("the donor entry carries decorators; they would be left behind");
        donorKids.RemoveAt(0);
        SetChildren(asset, DonorParent, donorKids);
        Console.WriteLine($"  [{DonorParent}] ORDER JUNGLE 2 sequence: dropped ClearTarget, {donorKids.Count} children left");

        // ---- 2. the chase child becomes the clear, under the same gate ----
        // Every child entry serializes all four fields with the unused reference written
        // null, so this is a two-reference edit and the entry's Decorators stay as they are.
        var chaseEntry = (StructPropertyData)chaseKids[0];
        if (Fields(chaseEntry) != Fields(donorEntry))
            throw new Exception($"child entry shape differs: chase [{Fields(chaseEntry)}] vs donor [{Fields(donorEntry)}]");
        SetEntryRef(chaseEntry, "ChildComposite", new FPackageIndex(0));
        SetEntryRef(chaseEntry, "ChildTask", Pkg(DonorClearTarget));
        SetObject(asset, DonorClearTarget, "ParentNode", Pkg(ChaseParent));
        Console.WriteLine($"  [{ChaseParent}] orderless engage: child 0 [{ChaseSubtree}] chase -> "
                        + $"[{DonorClearTarget}] ClearTarget, gate [{ChaseGateForce}]/[{ChaseGateRange}] kept");

        // ---- 3. unlink the early stop order ----
        var droppedDecorators = DecoratorsOf(bgKids[0]);
        bgKids.RemoveAt(0);
        SetChildren(asset, MoveBackground, bgKids);
        Console.WriteLine($"  [{MoveBackground}] move background: dropped the early IssueStopOrder "
                        + $"and its {droppedDecorators.Length} decorators, {bgKids.Count} child left");

        // ---- preload dependencies, kept to the cooker convention ----
        DepRemove(asset, DonorParent,  Pkg(DonorClearTarget));
        DepRemove(asset, ChaseParent,  Pkg(ChaseSubtree));
        DepAdd   (asset, ChaseParent,  Pkg(DonorClearTarget));
        DepSet   (asset, DonorClearTarget, Pkg(DonorParent), Pkg(ChaseParent));
        DepRemove(asset, MoveBackground, Pkg(EarlyStopOrder));
        foreach (var d in droppedDecorators) DepRemove(asset, MoveBackground, d);

        // ---- checks that do not need the game ----
        // An export written short makes the loader read the next one from the wrong offset,
        // so prove the trailing Extras bytes are untouched.
        var pristine = new UAsset(src, EngineVersion.VER_UE4_27);
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            int ours = asset.Exports[i].Extras?.Length ?? 0;
            int was  = pristine.Exports[i].Extras?.Length ?? 0;
            if (ours != was)
                throw new Exception($"export [{i}] Extras is {ours} bytes, vanilla has {was}");
        }
        A.Bind();
        Console.WriteLine($"  Extras preserved on all {asset.Exports.Count} exports");

        if (asset.Exports.Count != exportsBefore) throw new Exception("export count changed");
        if (asset.Imports.Count != importsBefore) throw new Exception("import count changed");
        if (asset.GetNameMapIndexList().Count != namesBefore) throw new Exception("name table changed");
        VerifyReachability(asset);

        var outDir  = Path.Combine(outRoot, AssetSubDir);
        var outPath = Path.Combine(outDir, AssetName + ".uasset");
        A.WriteAndVerify(outPath, "BlockAutoChase");

        var reloaded = new UAsset(outPath, EngineVersion.VER_UE4_27);
        VerifyReachability(reloaded);
        Console.WriteLine("  reloaded from disk: tree walk clean");
        return 0;
    }

    // ---------------- tree walk ----------------
    // Everything the game touches is reached from RootNode, so this proves the donor is
    // linked once, the unlinked nodes are gone, and no node is referenced twice.

    static void VerifyReachability(UAsset a)
    {
        int root = ((ObjectPropertyData)Prop(a, 0, "RootNode")).Value.Index - 1;
        var seen  = new HashSet<int>();
        var refs  = new Dictionary<int, int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            if (!seen.Add(n)) continue;
            if (a.Exports[n] is not NormalExport ne) continue;
            foreach (var p in ne.Data)
            {
                if (p.Name.Value.Value != "Children") continue;
                foreach (var el in ((ArrayPropertyData)p).Value)
                    foreach (var f in ((StructPropertyData)el).Value)
                    {
                        if (f is ObjectPropertyData o) Note(o, refs, stack);
                        else if (f is ArrayPropertyData arr)
                            foreach (var d in arr.Value)
                                if (d is ObjectPropertyData od) Note(od, refs, stack);
                    }
            }
        }
        if (!seen.Contains(DonorClearTarget))
            throw new Exception("donor ClearTarget is not reachable from RootNode");
        if (seen.Contains(ChaseSubtree))
            throw new Exception("the chase subtree is still reachable");
        if (seen.Contains(EarlyStopOrder))
            throw new Exception("the early IssueStopOrder is still reachable");
        foreach (var kv in refs)
            if (kv.Value != 1)
                throw new Exception($"export [{kv.Key}] is linked {kv.Value} times; a behavior tree node must be linked once");
        Console.WriteLine($"  reachable nodes {seen.Count} of {a.Exports.Count} exports "
                        + "(the chase subtree, the early stop order and their decorators are left unreferenced)");
    }

    static void Note(ObjectPropertyData o, Dictionary<int, int> refs, Stack<int> stack)
    {
        if (o.Value.Index == 0) return;
        int c = o.Value.Index - 1;
        refs[c] = refs.GetValueOrDefault(c) + 1;
        stack.Push(c);
    }

    // ---------------- small helpers over the property tree ----------------

    static FPackageIndex Pkg(int exportIndex) => FPackageIndex.FromExport(exportIndex);

    static void Expect(UAsset a, int i, string cls)
    {
        var got = a.Exports[i].GetExportClassType().Value.Value;
        if (got != cls) throw new Exception($"export [{i}] is {got}, expected {cls} - the behavior tree changed");
    }

    static PropertyData Prop(UAsset a, int export, string name)
        => ((NormalExport)a.Exports[export]).Data.First(p => p.Name.Value.Value == name);

    static List<PropertyData> Children(UAsset a, int export)
        => ((ArrayPropertyData)Prop(a, export, "Children")).Value.ToList();

    static void SetChildren(UAsset a, int export, List<PropertyData> kids)
        => ((ArrayPropertyData)Prop(a, export, "Children")).Value = kids.ToArray();

    static string Fields(StructPropertyData entry)
        => string.Join(", ", entry.Value.Select(f => f.Name.Value.Value));

    static int ChildTaskOf(PropertyData entry) => ChildRef(entry, "ChildTask");
    static int ChildCompositeOf(PropertyData entry) => ChildRef(entry, "ChildComposite");

    static int ChildRef(PropertyData entry, string field)
    {
        var f = ((StructPropertyData)entry).Value.FirstOrDefault(x => x.Name.Value.Value == field);
        return f is ObjectPropertyData o && o.Value != null ? o.Value.Index : 0;
    }

    static FPackageIndex[] DecoratorsOf(PropertyData entry)
    {
        var f = ((StructPropertyData)entry).Value.FirstOrDefault(x => x.Name.Value.Value == "Decorators");
        return f is ArrayPropertyData arr
            ? arr.Value.Cast<ObjectPropertyData>().Select(o => o.Value).ToArray()
            : Array.Empty<FPackageIndex>();
    }

    static void SetEntryRef(StructPropertyData entry, string field, FPackageIndex to)
    {
        var f = entry.Value.FirstOrDefault(x => x.Name.Value.Value == field)
             ?? throw new Exception($"child entry has no {field} field");
        ((ObjectPropertyData)f).Value = to;
    }

    static void SetObject(UAsset a, int export, string name, FPackageIndex to)
        => ((ObjectPropertyData)Prop(a, export, name)).Value = to;

    // ---------------- preload dependencies ----------------
    // Per export, the cooker lists every other export it points at, and the loader walks
    // that list, so a new reference without a matching entry may not exist yet when read.

    static void DepAdd(UAsset a, int export, FPackageIndex dep)
    {
        var l = a.Exports[export].CreateBeforeSerializationDependencies;
        if (!l.Any(x => x.Index == dep.Index)) l.Add(dep);
    }

    static void DepRemove(UAsset a, int export, FPackageIndex dep)
    {
        var l = a.Exports[export].CreateBeforeSerializationDependencies;
        int n = l.RemoveAll(x => x.Index == dep.Index);
        if (n != 1) throw new Exception($"expected one dependency {dep.Index} on export [{export}], removed {n}");
    }

    static void DepSet(UAsset a, int export, FPackageIndex from, FPackageIndex to)
    {
        DepRemove(a, export, from);
        DepAdd(a, export, to);
    }
}
