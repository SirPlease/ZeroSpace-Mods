using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using ZSPatchKit;
using static ZSPatchKit.Kis;

// ZS Mod Manager
//
// Builds the pak that adds a Mods page to the game's Settings screen, one section per mod.
//
//   dotnet run --project patcher -- <manifests> <original widgets> <output folder>
//
// Each mod describes its settings in a json manifest. This writes a registration asset per
// mod, which ships in that mod's own pak, and the Mods page, which imports those names and
// builds rows from whichever ones are installed.
//
// Settings live in a map on a save file under the slot name ZSModSettings, plus one plain
// property per setting. Extra sections are cloned at build time, so mod count is not capped.

class Setting
{
    public string key { get; set; } = "";
    public string label { get; set; } = "";
    public string type { get; set; } = "toggle";
    public bool master { get; set; }
    public bool @default { get; set; }
    public string defaultOption { get; set; } = "";
    public List<string>? options { get; set; }
    public string description { get; set; } = "";
}
class ModManifest
{
    public string id { get; set; } = "";
    public string name { get; set; } = "";
    public string pakBuild { get; set; } = "";      // where this mod's pak tree lives
    public List<string> options { get; set; } = new();   // shared dropdown options
    public string optionsFile { get; set; } = "";        // ...or a json file holding them
    public bool published { get; set; }                  // include in a --published build
    public List<Setting> settings { get; set; } = new();
}
class PaletteFile { public List<string> colors { get; set; } = new(); }

class Program
{
    // Where things come from and go. All three can be overridden on the command line:
    //   dotnet run --project patcher -- <manifests> <original widgets> <output folder>
    static string ManifestDir = null!;   // one .json per mod that has settings
    static string RawDir = null!;        // the original widgets, taken from the game
    static string PakBuild = null!;      // the Zerospace\Content\... tree to pack

    const string DefaultManifests = @"mods\modmanager\mods.d";
    const string DefaultRaw = @"mods\modmanager\raw";
    const string DefaultOutput = @"mods\dynmanager\pak_build";
    const string DefaultPublicOutput = @"mods\dynmanager\dist_build";
    static string OptionsOut => Path.Combine(PakBuild, @"Zerospace\Content\Nova\UI\Options");
    static string ModsOut => Path.Combine(PakBuild, @"Zerospace\Content\Mods\ZSModManager");
    static string RegistryOut => Path.Combine(PakBuild, @"Zerospace\Content\Mods\Registry");

    const string SlotName = "ZSModSettings";
    const string SavePkgPath = "/Game/Mods/ZSModManager/ZSModSettingsSave";
    const string SaveClsName = "ZSModSettingsSave_C";
    const string PagePkgPath = "/Game/Mods/ZSModManager/W_SettingsMenu_Mods";
    const string PageClsName = "W_SettingsMenu_Mods_C";
    // How many mods the page shows: however many manifests there are. The first four
    // sections come from the donor page, the rest are cloned at build time, so there is
    // no fixed ceiling.
    static int SlotCount;
    static List<ModManifest> Mods = new();

    // The four the donor page already has. Cloned ones are appended to this list by
    // BuildClonedSections(), so Sections[k] is always slot k's home.
    static readonly List<(string Section, string Header, string Container)> Sections = new()
    {
        ("LanguageTextSection", "LanguageTextHeader", "LanguageTextContainer"),
        ("CameraSection", "CameraHeader", "CameraContainer"),
        ("FormationDragSection", "FormationDragHeader", "FormationDragContainer"),
        ("GalaxyMapSection", "GalaxyMapHeader", "GalaxyMapContainer"),
    };
    const string CloneFrom = "CameraSection";   // header + container + the section box
    const string TopLevelBox = "SettingsBox";   // sections must be children of this
    static readonly string[] AlwaysCollapse =
    {
        "OverlaysSection", "SystemSection", "AdvancedToggleRow", "AdvancedRoot",
        "ResetDefaultsButton",
    };
    // spare handlers to repurpose, found by substring (numbers vary with renames)
    const string SharedToggleKey = "Row_Stat_ServerFPS";
    // Every mod's master switch binds to this one handler, repurposed from a blanked
    // row. It works out which mod fired from the setting name it is given.
    const string MasterKey = "Row_Stat_IdleTime";
    const string SharedDropdownKey = "Row_Language_K2Node";

    // A dropdown's options come from the setting itself, or from the list the mod
    // shares across its dropdowns ("options" in the manifest, or the file named by
    // "optionsFile"). Nothing here is shared between mods.
    static readonly Dictionary<string, List<string>> ModOptions = new();
    static List<string> OptionsFor(ModManifest m, Setting s) =>
        s.options ?? (ModOptions.TryGetValue(m.id, out var o) ? o : new List<string>());

    // A registration asset belongs in that mod's own pak; "pakBuild" in the manifest says
    // where. Without it the asset lands in registry\<ModId>\ for the author to copy.

    // The default paths are relative to the repo, and `dotnet run` starts in the
    // project folder, so climb up until the manifests come into view.
    static int Main(string[] args)
    {
        // --published: build the manager the public gets, which knows only the mods that
        // are actually published. Without it, every manifest is included.
        bool publishedOnly = args.Contains("--published");
        args = args.Where(a => a != "--published").ToArray();

        var root = Repo.Root(DefaultManifests);
        ManifestDir = args.Length > 0 ? args[0] : Path.Combine(root, DefaultManifests);
        RawDir = args.Length > 1 ? args[1] : Path.Combine(root, DefaultRaw);
        PakBuild = args.Length > 2 ? args[2] : Path.Combine(root, (publishedOnly ? DefaultPublicOutput : DefaultOutput));


        // Section order is the mod's display NAME, not its file name, so renaming a
        // manifest file does not reshuffle the page.
        var manifests = Directory.GetFiles(ManifestDir, "*.json")
            .Select(p => JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(p))!)
            .OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var m in manifests)
        {
            if (m.settings.Count == 0 || !m.settings[0].master || m.settings[0].type != "toggle")
                throw new Exception($"mod {m.id}: first setting must be the master toggle");
            var optionLists = m.settings.Where(s => s.type == "dropdown").Select(x => OptionsFor(m, x)).ToList();
            if (optionLists.Count > 0 && optionLists.Any(o => !o.SequenceEqual(optionLists[0])))
                throw new Exception($"mod {m.id}: all of a mod's dropdowns must share one option list");
        }
        if (publishedOnly) manifests = manifests.Where(m => m.published).ToList();
        foreach (var m in manifests)
        {
            if (m.options.Count > 0) ModOptions[m.id] = m.options;
            else if (!string.IsNullOrEmpty(m.optionsFile))
            {
                var f = Path.IsPathRooted(m.optionsFile) ? m.optionsFile : Path.Combine(root, m.optionsFile);
                if (!File.Exists(f)) throw new Exception($"mod {m.id}: optionsFile not found: {f}");
                ModOptions[m.id] = JsonSerializer.Deserialize<PaletteFile>(File.ReadAllText(f))!.colors;
            }
        }
        Mods = manifests;
        SlotCount = manifests.Count;
        if (manifests.Select(m => m.id).Distinct().Count() != SlotCount) throw new Exception("two mods share an id");
        Console.WriteLine(manifests.Count == 0
            ? "manifests: none, the Mods page will be empty"
            : $"manifests: {string.Join(", ", manifests.Select(m => $"{m.id} ({m.settings.Count} settings)"))}");

        Directory.CreateDirectory(OptionsOut);
        Directory.CreateDirectory(ModsOut);
        Directory.CreateDirectory(RegistryOut);

        BuildStoreClass(manifests);
        // The manager imports each mod's registration asset by name. One that is not
        // installed fails to resolve and its section stays hidden.
        if (Directory.Exists(RegistryOut)) Directory.Delete(RegistryOut, true);
        foreach (var m in manifests)
        {
            var tree = string.IsNullOrEmpty(m.pakBuild)
                ? Path.Combine(PakBuild, "..", "registry", m.id)
                : (Path.IsPathRooted(m.pakBuild) ? m.pakBuild : Path.Combine(Repo.Root(DefaultManifests), m.pakBuild));
            // wipe first, so a renamed mod never keeps shipping its old registration
            var reg = Path.Combine(tree, @"Zerospace\Content\Mods\Registry");
            if (Directory.Exists(reg)) Directory.Delete(reg, true);
            BuildSlotAsset(m, outDir: reg);
        }
        BuildModsPage();
        PatchContainers();
        Console.WriteLine("OK");
        return 0;
    }

    // ---------- shared helpers ----------

    static void RenameNames(UAsset asset, (string From, string To)[] renames)
    {
        var list = asset.GetNameMapIndexList();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i].ToString();
            var orig = s;
            foreach (var (from, to) in renames) s = s.Replace(from, to);
            if (s != orig) asset.SetNameReference(i, new FString(s));
        }
    }

    // ---------- property authoring (ALL common fields, always: the boot-crash rule) ----------

    static FProperty Finish(UAsset a, FProperty p, string name, string serializedType,
        EPropertyFlags flags = EPropertyFlags.CPF_None)
    {
        p.Name = FName.FromString(a, name);
        p.SerializedType = FName.FromString(a, serializedType);
        p.Flags = EObjectFlags.RF_Public;
        p.ArrayDim = EArrayDim.TArray;
        p.PropertyFlags = flags;
        p.RepNotifyFunc = FName.FromString(a, "None");
        p.BlueprintReplicationCondition = ELifetimeCondition.COND_None;
        return p;
    }

    static FProperty StrProp(UAsset a, string name, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        Finish(a, new FGenericProperty { ElementSize = 16 }, name, "StrProperty", f);
    static FProperty IntProp(UAsset a, string name, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        Finish(a, new FGenericProperty { ElementSize = 4 }, name, "IntProperty", f);
    static FProperty TextProp(UAsset a, string name) =>
        Finish(a, new FGenericProperty { ElementSize = 24 }, name, "TextProperty");
    static FProperty NameProp(UAsset a, string name) =>
        Finish(a, new FGenericProperty { ElementSize = 12 }, name, "NameProperty");
    static FProperty BoolProp(UAsset a, string name, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        Finish(a, new FBoolProperty
        {
            ElementSize = 1, FieldSize = 1, ByteOffset = 0, ByteMask = 1,
            FieldMask = 255, NativeBool = true, Value = false,
        }, name, "BoolProperty", f);
    static FProperty ObjProp(UAsset a, string name, FPackageIndex cls) =>
        Finish(a, new FObjectProperty { ElementSize = 8, PropertyClass = cls }, name, "ObjectProperty");
    static FProperty ArrayProp(UAsset a, string name, FProperty inner, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        Finish(a, new FArrayProperty { ElementSize = 16, Inner = inner }, name, "ArrayProperty", f);
    static FProperty StrArrayProp(UAsset a, string name, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        ArrayProp(a, name, StrProp(a, name), f);
    static FProperty IntArrayProp(UAsset a, string name, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        ArrayProp(a, name, IntProp(a, name), f);
    static FProperty TextArrayProp(UAsset a, string name) =>
        ArrayProp(a, name, TextProp(a, name));
    static FProperty MapStrStrProp(UAsset a, string name, EPropertyFlags f = EPropertyFlags.CPF_None) =>
        Finish(a, new FMapProperty
        {
            ElementSize = 80,
            KeyProp = StrProp(a, name),
            ValueProp = StrProp(a, name + "_Value"),
        }, name, "MapProperty", f);
    static FProperty DelegateProp(UAsset a, string name, FPackageIndex sig) =>
        Finish(a, new FDelegateProperty { ElementSize = 20, SignatureFunction = sig }, name, "DelegateProperty");

    // post-build audit: every authored property must carry the common fields
    static void AuditProps(UAsset a, IEnumerable<FProperty> props, string where)
    {
        foreach (var p in props)
        {
            void Chk(FProperty q, string tag)
            {
                if (q.ArrayDim != EArrayDim.TArray) throw new Exception($"{where}: {tag} ArrayDim wrong");
                if (q.RepNotifyFunc.ToString() != "None") throw new Exception($"{where}: {tag} RepNotifyFunc wrong");
                if ((q.Flags & EObjectFlags.RF_Public) == 0) throw new Exception($"{where}: {tag} not RF_Public");
                if (q is FMapProperty mq) { Chk(mq.KeyProp, tag + ".Key"); Chk(mq.ValueProp, tag + ".Value"); }
                if (q is FArrayProperty aq) Chk(aq.Inner, tag + ".Inner");
            }
            Chk(p, p.Name.ToString());
        }
    }

    // ---------- expression builders ----------

    // library call through a Default__ CDO: Context(ObjectConst(cdo), FinalFunction)
    // obj.<NativeFn>(args): final call through an object expression
    // ---------------- shared: mods\lib\ZSPatchKit ----------------
    // Forwarders that take the asset first, since this patcher builds several in one run.
    static int FindImport(UAsset a, string objectName, string className = "")
        => Imports.Find(a, objectName, className);
    static FPackageIndex AddImport(UAsset a, string classPackage, string className, FPackageIndex outer, string objectName)
        => Imports.Add(a, classPackage, className, outer, objectName);
    static FPackageIndex EnsurePackageImport(UAsset a, string pkgPath) => Imports.EnsurePackage(a, pkgPath);
    static FPackageIndex EnsureNativeClassImport(UAsset a, string scriptPkg, string cls) => Imports.EnsureClass(a, scriptPkg, cls);
    static FPackageIndex EnsureFunctionImport(UAsset a, string scriptPkg, string owningClass, string fn)
        => Imports.EnsureFn(a, scriptPkg, owningClass, fn);
    static FPackageIndex EnsureDefaultObject(UAsset a, string scriptPkg, string cls) => Imports.EnsureDefaultObject(a, scriptPkg, cls);

    // The asset argument on these three is unused; it keeps the call sites unchanged.
    static KismetPropertyPointer Ptr(UAsset a, FName name, FPackageIndex owner) => Kis.Ptr(name, owner);
    static EX_Context ReadMember(UAsset a, KismetExpression objExpr, FName prop, FPackageIndex ownerCls)
        => Kis.ReadMember(objExpr, prop, ownerCls);
    static EX_Context CallOn(UAsset a, KismetExpression objExpr, FPackageIndex fn, params KismetExpression[] args)
        => Kis.CallOn(objExpr, fn, null, args);

    // obj.<BPorVirtualFn>(args): name-resolved virtual call
    static EX_Context VCallOn(UAsset a, KismetExpression objExpr, string fn, params KismetExpression[] args)
    {
        var call = new EX_VirtualFunction { VirtualFunctionName = FName.FromString(a, fn), Parameters = args };
        return new EX_Context
        {
            ObjectExpression = objExpr,
            Offset = (uint)Measure(call),
            RValuePointer = NullPtr(),
            ContextExpression = call,
        };
    }

    // obj.<BPLocalFn>(args): BP-declared function on a widget (SetDropdownOptions)
    static EX_Context LCallOn(UAsset a, KismetExpression objExpr, string fn, params KismetExpression[] args)
    {
        var call = new EX_LocalVirtualFunction { VirtualFunctionName = FName.FromString(a, fn), Parameters = args };
        return new EX_Context
        {
            ObjectExpression = objExpr,
            Offset = (uint)Measure(call),
            RValuePointer = NullPtr(),
            ContextExpression = call,
        };
    }

    static EX_Let WriteMember(UAsset a, KismetExpression objExpr, FName prop, FPackageIndex ownerCls, KismetExpression value)
    {
        var iv = new EX_InstanceVariable { Variable = Ptr(a, prop, ownerCls) };
        return new EX_Let
        {
            Value = Ptr(a, prop, ownerCls),
            Variable = new EX_Context
            {
                ObjectExpression = objExpr,
                Offset = (uint)Measure(iv),
                RValuePointer = Ptr(a, prop, ownerCls),
                ContextExpression = iv,
            },
            Expression = value,
        };
    }

    static EX_AddMulticastDelegate AddMulticast(UAsset a, KismetExpression rowExpr, string delegateProp, FPackageIndex rowCls, KismetExpression delLocal)
    {
        var prop = FName.FromString(a, delegateProp);
        var iv = new EX_InstanceVariable { Variable = Ptr(a, prop, rowCls) };
        return new EX_AddMulticastDelegate
        {
            Delegate = new EX_Context
            {
                ObjectExpression = rowExpr,
                Offset = (uint)Measure(iv),
                RValuePointer = Ptr(a, prop, rowCls),
                ContextExpression = iv,
            },
            DelegateToAdd = delLocal,
        };
    }

    // assemble statements resolving symbolic labels (forward AND backward)
    static void SetBody(FunctionExport f, List<(string? Label, KismetExpression Ex, string? JumpTo)> body)
    {
        var offsets = new Dictionary<string, uint>();
        uint off = 0;
        foreach (var (label, ex, _) in body)
        {
            if (label != null) offsets[label] = off;
            off += (uint)Measure(ex);
        }
        foreach (var (_, ex, target) in body)
        {
            if (target == null) continue;
            uint t = offsets[target];
            switch (ex)
            {
                case EX_Jump j: j.CodeOffset = t; break;
                case EX_JumpIfNot jn: jn.CodeOffset = t; break;
                default: throw new Exception("label on non-jump");
            }
        }
        f.ScriptBytecode = body.Select(b => b.Ex).ToArray();
    }

    // ---------- the save class: the settings map, plus a property per setting ----------

    static void BuildStoreClass(List<ModManifest> mods)
    {
        Console.WriteLine("=== ZSModSettingsSave (store: legacy typed props + SettingsMap) ===");
        var asset = new UAsset(Path.Combine(RawDir, "BP_ListItemObj_MapInfo.uasset"), EngineVersion.VER_UE4_27);
        if (!asset.VerifyBinaryEquality()) throw new Exception("store donor: round-trip not binary-equal");

        RenameNames(asset, new[]
        {
            ("/Game/Nova/UI/MainMenu/BP_ListItemObj_MapInfo", SavePkgPath),
            ("BP_ListItemObj_MapInfo", "ZSModSettingsSave"),
        });

        var cls = asset.Exports.OfType<ClassExport>().Single();
        var cdo = asset.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));

        int oldParent = cls.SuperStruct.Index;
        var saveGameCls = EnsureNativeClassImport(asset, "/Script/Engine", "SaveGame");
        cls.SuperStruct = saveGameCls;
        foreach (var ex in asset.Exports)
        {
            Remap(ex.SerializationBeforeSerializationDependencies, oldParent, saveGameCls.Index);
            Remap(ex.CreateBeforeSerializationDependencies, oldParent, saveGameCls.Index);
            Remap(ex.SerializationBeforeCreateDependencies, oldParent, saveGameCls.Index);
            Remap(ex.CreateBeforeCreateDependencies, oldParent, saveGameCls.Index);
        }

        const EPropertyFlags SaveFlags = EPropertyFlags.CPF_Edit | EPropertyFlags.CPF_BlueprintVisible | EPropertyFlags.CPF_SaveGame;
        var props = cls.LoadedProperties.ToList();
        props.RemoveAll(p => p.Name.ToString() == "MapInfo");
        cdo.Data.RemoveAll(p => p.Name.ToString() == "MapInfo");

        // one plain property per setting, for mods that read properties rather than the
        // map. Defaults come from the manifests.
        foreach (var m in mods)
            foreach (var s in m.settings)
            {
                string baseName = $"{m.id}_{s.key}";
                if (s.type == "toggle")
                {
                    props.Add(BoolProp(asset, baseName, SaveFlags));
                    cdo.Data.Add(new BoolPropertyData(FName.FromString(asset, baseName)) { Value = s.@default });
                }
                else
                {
                    props.Add(StrProp(asset, baseName, SaveFlags));
                    props.Add(IntProp(asset, baseName + "Idx", SaveFlags));
                    cdo.Data.Add(new StrPropertyData(FName.FromString(asset, baseName)) { Value = new FString(s.defaultOption) });
                    cdo.Data.Add(new IntPropertyData(FName.FromString(asset, baseName + "Idx")) { Value = OptionsFor(m, s).IndexOf(s.defaultOption) });
                }
            }
        // the generic store
        props.Add(MapStrStrProp(asset, "SettingsMap", SaveFlags));
        cls.LoadedProperties = props.ToArray();
        AuditProps(asset, cls.LoadedProperties, "store class");

        var outPath = Path.Combine(ModsOut, "ZSModSettingsSave.uasset");
        asset.Write(outPath);
        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        var ccls = check.Exports.OfType<ClassExport>().Single();
        AuditProps(check, ccls.LoadedProperties, "store class (reloaded)");
        if (check.Imports[-ccls.SuperStruct.Index - 1].ObjectName.ToString() != "SaveGame")
            throw new Exception("store: parent wrong after reload");
        Console.WriteLine($"  reloaded: parent=SaveGame, {ccls.LoadedProperties.Length} props (incl. SettingsMap)");
    }

    // ---------- widget cloning (used to add settings sections past the donor's four) ----------
    // A section is three widgets plus a slot export each. Cloning one copies all six,
    // repoints them at each other, and adds the three names to the page class.

    static NormalExport CloneExport(NormalExport src, string newName)
    {
        var dst = new NormalExport(page, src.Extras?.ToArray())
        {
            ObjectName = FName.FromString(page, newName),
            OuterIndex = src.OuterIndex,
            ClassIndex = src.ClassIndex,
            SuperIndex = src.SuperIndex,
            TemplateIndex = src.TemplateIndex,
            ObjectFlags = src.ObjectFlags,
            bForcedExport = src.bForcedExport,
            bNotForClient = src.bNotForClient,
            bNotForServer = src.bNotForServer,
            PackageGuid = src.PackageGuid,
            IsInheritedInstance = src.IsInheritedInstance,
            PackageFlags = src.PackageFlags,
            bNotAlwaysLoadedForEditorGame = src.bNotAlwaysLoadedForEditorGame,
            bIsAsset = src.bIsAsset,
            GeneratePublicHash = src.GeneratePublicHash,
            ObjectGuid = src.ObjectGuid,
            SerializationControl = src.SerializationControl,
            Operation = src.Operation,
            HasLeadingFourNullBytes = src.HasLeadingFourNullBytes,
            SerializationBeforeSerializationDependencies = new List<FPackageIndex>(src.SerializationBeforeSerializationDependencies),
            CreateBeforeSerializationDependencies = new List<FPackageIndex>(src.CreateBeforeSerializationDependencies),
            SerializationBeforeCreateDependencies = new List<FPackageIndex>(src.SerializationBeforeCreateDependencies),
            CreateBeforeCreateDependencies = new List<FPackageIndex>(src.CreateBeforeCreateDependencies),
        };
        dst.Data = src.Data.Select(d => (PropertyData)d.Clone()).ToList();
        page.Exports.Add(dst);
        return dst;
    }

    static List<FPackageIndex>[] DepLists(Export e) => new[]
    {
        e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies,
        e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies,
    };

    static ObjectPropertyData OProp(NormalExport ex, string name) =>
        (ObjectPropertyData)ex.Data.First(d => d.Name.ToString() == name);

    static int ExportIdx(NormalExport ex) => page.Exports.IndexOf(ex) + 1;   // 1-based

    // Give the page class a widget-binding property for a cloned widget, copied from
    // the property the original widget already has.
    static void AddWidgetBinding(string srcName, string newName)
    {
        var cls = (ClassExport)page.Exports[pageClassExportIdx];
        var tpl = cls.LoadedProperties.OfType<FObjectProperty>().First(pr => pr.Name.ToString() == srcName);
        cls.LoadedProperties = cls.LoadedProperties.Append(new FObjectProperty
        {
            Name = FName.FromString(page, newName),
            SerializedType = tpl.SerializedType,
            Flags = tpl.Flags,
            ArrayDim = tpl.ArrayDim,
            ElementSize = tpl.ElementSize,
            PropertyFlags = tpl.PropertyFlags,
            RepIndex = tpl.RepIndex,
            RepNotifyFunc = FName.FromString(page, "None"),
            BlueprintReplicationCondition = tpl.BlueprintReplicationCondition,
            PropertyClass = tpl.PropertyClass,
        }).ToArray();
    }

    // Copy one widget together with its slot. The clone keeps the same parent as the
    // original unless the caller moves it.
    static (NormalExport W, NormalExport S) ClonePair(NormalExport srcW, string newName)
    {
        int srcWIdx = ExportIdx(srcW);
        int srcSIdx = OProp(srcW, "Slot").Value.Index;
        var srcS = (NormalExport)page.Exports[srcSIdx - 1];

        var dstW = CloneExport(srcW, newName);
        int dstWIdx = page.Exports.Count;
        var dstS = CloneExport(srcS, srcS.ObjectName.ToString() + "_" + newName);
        int dstSIdx = page.Exports.Count;

        OProp(dstW, "Slot").Value = new FPackageIndex(dstSIdx);
        OProp(dstS, "Content").Value = new FPackageIndex(dstWIdx);

        foreach (var lst in DepLists(dstW).Concat(DepLists(dstS)))
        {
            Remap(lst, srcWIdx, dstWIdx);
            Remap(lst, srcSIdx, dstSIdx);
        }
        // whatever else depended on the original pair now also depends on the copy
        foreach (var ex in page.Exports)
        {
            if (ReferenceEquals(ex, dstW) || ReferenceEquals(ex, dstS)) continue;
            foreach (var lst in DepLists(ex))
            {
                if (lst.Any(x => x.Index == srcWIdx) && !lst.Any(x => x.Index == dstWIdx)) lst.Add(new FPackageIndex(dstWIdx));
                if (lst.Any(x => x.Index == srcSIdx) && !lst.Any(x => x.Index == dstSIdx)) lst.Add(new FPackageIndex(dstSIdx));
            }
        }
        AddWidgetBinding(srcW.ObjectName.ToString(), newName);
        return (dstW, dstS);
    }

    // Clone the whole section trio and hang it off the page's top-level box.
    static (string Section, string Header, string Container) CloneSection(int n)
    {
        var srcSection = Widget(CloneFrom);
        var srcHeader = Widget(Sections[1].Header);
        var srcContainer = Widget(Sections[1].Container);
        string sec = $"ZSDM_Section{n:D2}", hdr = $"ZSDM_Header{n:D2}", cont = $"ZSDM_Container{n:D2}";

        var (dstSec, dstSecSlot) = ClonePair(srcSection, sec);
        var (dstHdr, dstHdrSlot) = ClonePair(srcHeader, hdr);
        var (dstCont, dstContSlot) = ClonePair(srcContainer, cont);

        int secIdx = ExportIdx(dstSec);
        // the header and container live inside the cloned section, not the original
        foreach (var (childSlot, childW) in new[] { (dstHdrSlot, dstHdr), (dstContSlot, dstCont) })
        {
            OProp(childSlot, "Parent").Value = new FPackageIndex(secIdx);
            if (childSlot.OuterIndex.Index == ExportIdx(srcSection)) childSlot.OuterIndex = new FPackageIndex(secIdx);
        }
        // and the section lists exactly those two child slots
        var slots = (ArrayPropertyData)dstSec.Data.First(d => d.Name.ToString() == "Slots");
        slots.Value = new PropertyData[]
        {
            NewObjRef(slots, "Slots", ExportIdx(dstHdrSlot)),
            NewObjRef(slots, "Slots", ExportIdx(dstContSlot)),
        };

        // hang the section itself under the page's top-level box
        var box = Widget(TopLevelBox);
        int boxIdx = ExportIdx(box);
        OProp(dstSecSlot, "Parent").Value = new FPackageIndex(boxIdx);
        if (dstSecSlot.OuterIndex.Index == ExportIdx(Widget(TopLevelBox))) { }
        dstSecSlot.OuterIndex = new FPackageIndex(boxIdx);
        var boxSlots = (ArrayPropertyData)box.Data.First(d => d.Name.ToString() == "Slots");
        boxSlots.Value = boxSlots.Value.Append(NewObjRef(boxSlots, "Slots", ExportIdx(dstSecSlot))).ToArray();

        Console.WriteLine($"  cloned section {sec} (+{hdr}, {cont}) under {TopLevelBox}");
        return (sec, hdr, cont);
    }

    static ObjectPropertyData NewObjRef(ArrayPropertyData tpl, string name, int exportIdx)
    {
        var o = (ObjectPropertyData)((ObjectPropertyData)tpl.Value[0]).Clone();
        o.Name = FName.FromString(page, name);
        o.Value = new FPackageIndex(exportIdx);
        return o;
    }

    static void BuildClonedSections()
    {
        for (int n = Sections.Count; n < SlotCount; n++)
            Sections.Add(CloneSection(n + 1));
    }

    static void Remap(List<FPackageIndex> deps, int from, int to)
    {
        for (int i = 0; i < deps.Count; i++)
            if (deps[i].Index == from) deps[i] = new FPackageIndex(to);
    }

    // ---------- stage 2: registration slot assets ----------

    static void BuildSlotAsset(ModManifest m, string outDir)
    {
        string slotN = m.id;
        Console.WriteLine($"=== registration {slotN} -> {outDir} ===");
        var a = new UAsset(Path.Combine(RawDir, "BP_ListItemObj_MapInfo.uasset"), EngineVersion.VER_UE4_27);
        if (!a.VerifyBinaryEquality()) throw new Exception("slot donor: round-trip not binary-equal");

        RenameNames(a, new[]
        {
            ("/Game/Nova/UI/MainMenu/BP_ListItemObj_MapInfo", $"/Game/Mods/Registry/{slotN}"),
            ("BP_ListItemObj_MapInfo", slotN),
        });

        var cls = a.Exports.OfType<ClassExport>().Single();
        var cdo = a.Exports.OfType<NormalExport>().Single(e => e.ObjectName.ToString().StartsWith("Default__"));
        const EPropertyFlags F = EPropertyFlags.CPF_Edit | EPropertyFlags.CPF_BlueprintVisible;

        var props = cls.LoadedProperties.ToList();
        props.RemoveAll(p => p.Name.ToString() == "MapInfo");
        cdo.Data.RemoveAll(p => p.Name.ToString() == "MapInfo");

        props.Add(StrProp(a, "ZSREG_Name", F));
        props.Add(StrArrayProp(a, "ZSREG_Keys", F));
        props.Add(StrArrayProp(a, "ZSREG_Labels", F));
        props.Add(StrArrayProp(a, "ZSREG_Tips", F));
        props.Add(StrArrayProp(a, "ZSREG_Defaults", F));
        props.Add(IntArrayProp(a, "ZSREG_Types", F));
        props.Add(StrArrayProp(a, "ZSREG_Options", F));
        cls.LoadedProperties = props.ToArray();
        AuditProps(a, cls.LoadedProperties, slotN);

        PropertyData[] StrArr(string prop, IEnumerable<string> vals) =>
            vals.Select((v, i) => (PropertyData)new StrPropertyData(FName.FromString(a, prop)) { ArrayIndex = i, Value = new FString(v) }).ToArray();

        cdo.Data.Add(new StrPropertyData(FName.FromString(a, "ZSREG_Name")) { Value = new FString(m.name) });
        void AddArr(string prop, IEnumerable<string> vals, string type = "StrProperty")
        {
            var nm = FName.FromString(a, prop);
            cdo.Data.Add(new ArrayPropertyData(nm) { ArrayType = FName.FromString(a, type), Value = StrArr(prop, vals) });
        }
        AddArr("ZSREG_Keys", m.settings.Select(s => $"{m.id}_{s.key}"));
        AddArr("ZSREG_Labels", m.settings.Select(s => s.label));
        AddArr("ZSREG_Tips", m.settings.Select(s => s.description));
        AddArr("ZSREG_Defaults", m.settings.Select(s =>
            s.type == "toggle" ? (s.@default ? "1" : "0") : OptionsFor(m, s).IndexOf(s.defaultOption).ToString()));
        var tnm = FName.FromString(a, "ZSREG_Types");
        cdo.Data.Add(new ArrayPropertyData(tnm)
        {
            ArrayType = FName.FromString(a, "IntProperty"),
            Value = m.settings.Select((s, i) => (PropertyData)new IntPropertyData(tnm) { ArrayIndex = i, Value = s.type == "dropdown" ? 1 : 0 }).ToArray(),
        });
        var dropdowns = m.settings.Where(s => s.type == "dropdown").ToList();
        AddArr("ZSREG_Options", dropdowns.Count > 0 ? OptionsFor(m, dropdowns[0]) : new List<string>());

        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, slotN + ".uasset");
        a.Write(outPath);
        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        var ccls = check.Exports.OfType<ClassExport>().Single();
        AuditProps(check, ccls.LoadedProperties, slotN + " (reloaded)");
        if (ccls.ObjectName.ToString() != slotN + "_C") throw new Exception("slot class name wrong");
        Console.WriteLine($"  {slotN}_C: {m.settings.Count} settings, {dropdowns.Count} dropdowns");
    }

    // ---------- stage 3: the Mods page ----------

    static UAsset page = null!;
    static int pageClassExportIdx;

    static NormalExport Widget(string name) =>
        page.Exports.OfType<NormalExport>().FirstOrDefault(e => !(e is FunctionExport) && e.ObjectName.ToString() == name)
        ?? throw new Exception("widget not found: " + name);

    static FunctionExport Fn(string name) =>
        page.Exports.OfType<FunctionExport>().FirstOrDefault(e => e.ObjectName.ToString() == name)
        ?? throw new Exception("function not found: " + name);

    static FunctionExport FnByKey(string key)
    {
        var hits = page.Exports.OfType<FunctionExport>()
            .Where(e => e.ObjectName.ToString().Contains(key) && e.ObjectName.ToString().Contains("SettingChanged") ||
                        e.ObjectName.ToString().Contains(key) && e.ObjectName.ToString().Contains("SettingSelected")).ToList();
        if (hits.Count != 1) throw new Exception($"handler key '{key}' matched {hits.Count} functions: {string.Join(", ", hits.Select(h => h.ObjectName))}");
        return hits[0];
    }

    static Func<KismetExpression> AddLocal(FunctionExport f, FProperty prop)
    {
        var name = prop.Name;
        if (!f.LoadedProperties.Any(p => p.Name.ToString() == name.ToString()))
            f.LoadedProperties = f.LoadedProperties.Append(prop).ToArray();
        int idx = page.Exports.IndexOf(f);
        return () => new EX_LocalVariable { Variable = Ptr(page, name, new FPackageIndex(idx + 1)) };
    }

    static KismetExpression PVar(FunctionExport f, string local)
    {
        int idx = page.Exports.IndexOf(f);
        if (!f.LoadedProperties.Any(p => p.Name.ToString() == local))
            throw new Exception($"{f.ObjectName}: no local named {local}");
        return new EX_LocalVariable { Variable = Ptr(page, FName.FromString(page, local), new FPackageIndex(idx + 1)) };
    }

    static KismetExpression PageVar(string prop) =>
        new EX_InstanceVariable { Variable = Ptr(page, FName.FromString(page, prop), new FPackageIndex(pageClassExportIdx + 1)) };

    static void BuildModsPage()
    {
        Console.WriteLine("=== W_SettingsMenu_Mods (dynamic) ===");
        page = new UAsset(Path.Combine(RawDir, "W_SettingsMenu_General.uasset"), EngineVersion.VER_UE4_27);
        if (!page.VerifyBinaryEquality()) throw new Exception("page donor: round-trip not binary-equal");
        KismetSerializer.asset = page;

        RenameNames(page, new[]
        {
            ("/Game/Nova/UI/Options/W_SettingsMenu_General", PagePkgPath),
            ("W_SettingsMenu_General", "W_SettingsMenu_Mods"),
        });
        pageClassExportIdx = page.Exports.FindIndex(e => e is ClassExport);

        // extra sections, cloned before anything reads the Sections list
        BuildClonedSections();

        // ---- design-time cleanup ----
        var rowAvx = Widget("Row_AVX");
        var visibilityTemplate = rowAvx.Data.First(p => p.Name.ToString() == "Visibility");
        var rowClientFps = Widget("Row_Stat_ClientFPS");
        var inlineTextTemplate = ((ArrayPropertyData)rowClientFps.Data.First(p => p.Name.ToString() == "SettingValueDisplayNames")).Value[0];
        int textKeyCounter = 0;
        void SetTextProp(NormalExport ex, string propName, string source)
        {
            ex.Data.RemoveAll(p => p.Name.ToString() == propName);
            var t = (TextPropertyData)inlineTextTemplate.Clone();
            t.Name = FName.FromString(page, propName);
            t.ArrayIndex = 0;
            t.Value = new FString($"ZSDM_{++textKeyCounter:D4}");
            t.CultureInvariantString = new FString(source);
            ex.Data.Add(t);
        }
        void Collapse(NormalExport ex)
        {
            ex.Data.RemoveAll(p => p.Name.ToString() == "Visibility");
            ex.Data.Add((PropertyData)visibilityTemplate.Clone());
        }

        foreach (var name in AlwaysCollapse) Collapse(Widget(name));
        // blank the four host headers (runtime sets the real names)
        foreach (var (_, header, _) in Sections) SetTextProp(Widget(header), "Text", "");
        // collapse + blank EVERY design-time settings row (all rows are runtime-built now)
        foreach (var ex in page.Exports.OfType<NormalExport>().Where(e =>
                     !(e is FunctionExport) && e.GetExportClassType().ToString().StartsWith("W_OptionsMenuSetting_")))
        {
            Collapse(ex);
            bool slider = ex.GetExportClassType().ToString().Contains("Slider");
            SetTextProp(ex, slider ? "DisplayText" : "SettingDisplayName", "");
            SetTextProp(ex, slider ? "DescriptionText" : "SettingDisplayDescription", "");
        }
        Console.WriteLine("  design-time: host headers blanked, all rows collapsed+blanked");

        // ---- imports ----
        var savePkg = EnsurePackageImport(page, SavePkgPath);
        int saveIdx = FindImport(page, SaveClsName);
        var saveCls = saveIdx != 0 ? new FPackageIndex(saveIdx)
            : AddImport(page, "/Script/Engine", "BlueprintGeneratedClass", savePkg, SaveClsName);
        var fnLoad = EnsureFunctionImport(page, "/Script/Engine", "GameplayStatics", "LoadGameFromSlot");
        var fnSave = EnsureFunctionImport(page, "/Script/Engine", "GameplayStatics", "SaveGameToSlot");
        var fnCreate = EnsureFunctionImport(page, "/Script/Engine", "GameplayStatics", "CreateSaveGameObject");
        var fnIsValid = EnsureFunctionImport(page, "/Script/Engine", "KismetSystemLibrary", "IsValid");
        var fnSetNameP = EnsureFunctionImport(page, "/Script/Engine", "KismetSystemLibrary", "SetNamePropertyByName");
        var fnSetTextP = EnsureFunctionImport(page, "/Script/Engine", "KismetSystemLibrary", "SetTextPropertyByName");
        var fnSetIntP = EnsureFunctionImport(page, "/Script/Engine", "KismetSystemLibrary", "SetIntPropertyByName");
        var arrLibObj = EnsureDefaultObject(page, "/Script/Engine", "KismetArrayLibrary");
        var fnArrLen = EnsureFunctionImport(page, "/Script/Engine", "KismetArrayLibrary", "Array_Length");
        var fnArrGet = EnsureFunctionImport(page, "/Script/Engine", "KismetArrayLibrary", "Array_Get");
        var fnArrAdd = EnsureFunctionImport(page, "/Script/Engine", "KismetArrayLibrary", "Array_Add");
        var fnArrClear = EnsureFunctionImport(page, "/Script/Engine", "KismetArrayLibrary", "Array_Clear");
        var fnSetArrP = EnsureFunctionImport(page, "/Script/Engine", "KismetArrayLibrary", "SetArrayPropertyByName");
        var mapLibObj = EnsureDefaultObject(page, "/Script/Engine", "BlueprintMapLibrary");
        var fnMapAdd = EnsureFunctionImport(page, "/Script/Engine", "BlueprintMapLibrary", "Map_Add");
        var fnMapFind = EnsureFunctionImport(page, "/Script/Engine", "BlueprintMapLibrary", "Map_Find");
        var fnS2Name = EnsureFunctionImport(page, "/Script/Engine", "KismetStringLibrary", "Conv_StringToName");
        var fnS2I = EnsureFunctionImport(page, "/Script/Engine", "KismetStringLibrary", "Conv_StringToInt");
        var fnI2S = EnsureFunctionImport(page, "/Script/Engine", "KismetStringLibrary", "Conv_IntToString");
        var fnName2S = EnsureFunctionImport(page, "/Script/Engine", "KismetStringLibrary", "Conv_NameToString");
        var fnS2T = EnsureFunctionImport(page, "/Script/Engine", "KismetTextLibrary", "Conv_StringToText");
        var fnLess = EnsureFunctionImport(page, "/Script/Engine", "KismetMathLibrary", "Less_IntInt");
        var fnEq = EnsureFunctionImport(page, "/Script/Engine", "KismetMathLibrary", "EqualEqual_IntInt");
        var fnGreater = EnsureFunctionImport(page, "/Script/Engine", "KismetMathLibrary", "Greater_IntInt");
        var fnAdd = EnsureFunctionImport(page, "/Script/Engine", "KismetMathLibrary", "Add_IntInt");
        var wblObj = EnsureDefaultObject(page, "/Script/UMG", "WidgetBlueprintLibrary");
        var fnWCreate = EnsureFunctionImport(page, "/Script/UMG", "WidgetBlueprintLibrary", "Create");
        var fnAddChild = EnsureFunctionImport(page, "/Script/UMG", "VerticalBox", "AddChildToVerticalBox");
        var fnClearKids = EnsureFunctionImport(page, "/Script/UMG", "PanelWidget", "ClearChildren");
        var fnKidCount = EnsureFunctionImport(page, "/Script/UMG", "PanelWidget", "GetChildrenCount");
        var fnKidAt = EnsureFunctionImport(page, "/Script/UMG", "PanelWidget", "GetChildAt");
        var fnGCDO = EnsureFunctionImport(page, "/Script/RTS", "NovaBlueprintLibrary", "GetNovaClassDefaultObject");
        var clsObject = EnsureNativeClassImport(page, "/Script/CoreUObject", "Object");

        // row classes (present via the donor rows' export classes)
        var discreteCls = Widget("Row_Subtitles").ClassIndex;
        var dropdownCls = Widget("Row_Language").ClassIndex;
        int sigT = FindImport(page, "SettingChanged__DelegateSignature");
        var sigToggle = sigT != 0 ? new FPackageIndex(sigT)
            : AddImport(page, "/Script/CoreUObject", "Function", discreteCls, "SettingChanged__DelegateSignature");
        int sigD = FindImport(page, "SettingSelected__DelegateSignature");
        var sigDrop = sigD != 0 ? new FPackageIndex(sigD)
            : AddImport(page, "/Script/CoreUObject", "Function", dropdownCls, "SettingSelected__DelegateSignature");

        // One import per known mod, by id. A mod that is not installed fails to resolve
        // and its section stays hidden, which is how a mixed install works.
        var slotCls = new FPackageIndex[SlotCount];
        for (int k = 0; k < SlotCount; k++)
        {
            string id = Mods[k].id;
            var pkg = EnsurePackageImport(page, $"/Game/Mods/Registry/{id}");
            slotCls[k] = AddImport(page, "/Script/Engine", "BlueprintGeneratedClass", pkg, id + "_C");
        }

        // ---- handler rewrites ----
        var sharedToggle = FnByKey(SharedToggleKey);
        var sharedDropdown = FnByKey(SharedDropdownKey);
        var master = FnByKey(MasterKey);
        var fnNameEq = EnsureFunctionImport(page, "/Script/Engine", "KismetMathLibrary", "EqualEqual_NameName");
        var mapProp = FName.FromString(page, "SettingsMap");

        var fnSetBoolP = EnsureFunctionImport(page, "/Script/Engine", "KismetSystemLibrary", "SetBoolPropertyByName");
        var fnSetStrP = EnsureFunctionImport(page, "/Script/Engine", "KismetSystemLibrary", "SetStringPropertyByName");
        var fnConcat = EnsureFunctionImport(page, "/Script/Engine", "KismetStringLibrary", "Concat_StrStr");

        // Write the value into the map, and into the plain property of the same name for
        // mods that read properties. The second write is a no-op when it is absent.
        List<(string?, KismetExpression, string?)> StoreWrite(FunctionExport f, Func<KismetExpression> valueInt, bool dropdown)
        {
            var objL = AddLocal(f, ObjProp(page, "ZSDM_Obj", EnsureNativeClassImport(page, "/Script/Engine", "SaveGame")));
            var mapL = AddLocal(f, MapStrStrProp(page, "ZSDM_Map"));
            var sL = AddLocal(f, StrProp(page, "ZSDM_S"));
            var vL = AddLocal(f, StrProp(page, "ZSDM_V"));
            var bL = AddLocal(f, BoolProp(page, "ZSDM_B"));
            int fi = page.Exports.IndexOf(f);
            KismetPropertyPointer LP(string n) => Ptr(page, FName.FromString(page, n), new FPackageIndex(fi + 1));
            var body = new List<(string?, KismetExpression, string?)>
            {
                (null, new EX_LetObj { VariableExpression = objL(), AssignmentExpression = Call(fnLoad, Str(SlotName), Int(0)) }, null),
                (null, new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnIsValid, objL()) }, null),
                (null, new EX_JumpIfNot { BooleanExpression = bL() }, "create"),
                (null, new EX_Jump(), "have"),
                ("create", new EX_LetObj { VariableExpression = objL(), AssignmentExpression = Call(fnCreate, new EX_ObjectConst { Value = saveCls }) }, null),
                ("have", new EX_Let { Value = LP("ZSDM_Map"), Variable = mapL(), Expression = ReadMember(page, objL(), mapProp, saveCls) }, null),
                (null, new EX_Let { Value = LP("ZSDM_S"), Variable = sL(), Expression = Call(fnName2S, PVar(f, "SettingName")) }, null),
                (null, new EX_Let { Value = LP("ZSDM_V"), Variable = vL(), Expression = Call(fnI2S, valueInt()) }, null),
                (null, LibCall(mapLibObj, fnMapAdd, mapL(), sL(), vL()), null),
                (null, WriteMember(page, objL(), mapProp, saveCls, mapL()), null),
            };
            if (dropdown)
            {
                var nmIL = AddLocal(f, NameProp(page, "ZSDM_NmI"));
                body.Add((null, new EX_Let { Value = LP("ZSDM_V"), Variable = vL(), Expression = Call(fnConcat, sL(), Str("Idx")) }, null));
                body.Add((null, new EX_Let { Value = LP("ZSDM_NmI"), Variable = nmIL(), Expression = Call(fnS2Name, vL()) }, null));
                body.Add((null, Call(fnSetStrP, objL(), PVar(f, "SettingName"), PVar(f, "SettingValue")), null));
                body.Add((null, Call(fnSetIntP, objL(), nmIL(), PVar(f, "Index")), null));
            }
            else
            {
                var twL = AddLocal(f, BoolProp(page, "ZSDM_TW"));
                body.Add((null, new EX_LetBool { VariableExpression = twL(), AssignmentExpression = Call(fnGreater, valueInt(), Int(0)) }, null));
                body.Add((null, Call(fnSetBoolP, objL(), PVar(f, "SettingName"), twL()), null));
            }
            body.Add((null, new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnSave, objL(), Str(SlotName), Int(0)) }, null));
            return body;
        }

        void RewriteToggleHandler(FunctionExport f, bool isMaster)
        {
            if (!f.LoadedProperties.Any(p => p.Name.ToString() == "NewValue"))
                throw new Exception($"{f.ObjectName}: params are [{string.Join(", ", f.LoadedProperties.Select(p => p.Name))}]");
            var ret = f.ScriptBytecode.First(e => e is EX_Return);
            var end = f.ScriptBytecode.First(e => e is EX_EndOfScript);
            var body = StoreWrite(f, () => PVar(f, "NewValue"), dropdown: false);
            if (isMaster)
            {
                // Every mod's master switch lands here, and the delegate passes no sender,
                // so the setting name says which mod fired.
                var iL = AddLocal(f, IntProp(page, "ZSDM_I"));
                var nL = AddLocal(f, IntProp(page, "ZSDM_N"));
                var childL = AddLocal(f, ObjProp(page, "ZSDM_Child", clsObject));
                var onL = AddLocal(f, BoolProp(page, "ZSDM_On"));
                var b2L = AddLocal(f, BoolProp(page, "ZSDM_B2"));
                int fi = page.Exports.IndexOf(f);
                KismetPropertyPointer LP(string n) => Ptr(page, FName.FromString(page, n), new FPackageIndex(fi + 1));
                body.Add((null, new EX_LetBool { VariableExpression = onL(), AssignmentExpression = Call(fnGreater, PVar(f, "NewValue"), Int(0)) }, null));
                for (int k = 0; k < SlotCount; k++)
                {
                    string kk = "m" + k, container = Sections[k].Container;
                    string masterKey = $"{Mods[k].id}_{Mods[k].settings[0].key}";
                    body.Add((null, new EX_LetBool { VariableExpression = b2L(), AssignmentExpression =
                        Call(fnNameEq, PVar(f, "SettingName"), new EX_NameConst { Value = FName.FromString(page, masterKey) }) }, null));
                    body.Add((null, new EX_JumpIfNot { BooleanExpression = b2L() }, kk + "next"));
                    body.Add((null, new EX_Let { Value = LP("ZSDM_N"), Variable = nL(), Expression = CallOn(page, PageVar(container), fnKidCount) }, null));
                    body.Add((null, new EX_Let { Value = LP("ZSDM_I"), Variable = iL(), Expression = Int(1) }, null));
                    body.Add((kk + "loop", new EX_LetBool { VariableExpression = b2L(), AssignmentExpression = Call(fnLess, iL(), nL()) }, null));
                    body.Add((null, new EX_JumpIfNot { BooleanExpression = b2L() }, "gend"));
                    body.Add((null, new EX_LetObj { VariableExpression = childL(), AssignmentExpression = CallOn(page, PageVar(container), fnKidAt, iL()) }, null));
                    body.Add((null, VCallOn(page, childL(), "SetIsEnabled", onL()), null));
                    body.Add((null, new EX_Let { Value = LP("ZSDM_I"), Variable = iL(), Expression = Call(fnAdd, iL(), Int(1)) }, null));
                    body.Add((null, new EX_Jump(), kk + "loop"));
                    body.Add((kk + "next", new EX_Nothing(), null));
                }
                body.Add(("gend", ret, null));
            }
            else
                body.Add(("gend", ret, null));
            body.Add((null, end, null));
            SetBody(f, body);
            Console.WriteLine($"  rewrote {(isMaster ? $"shared master ({SlotCount} mods)" : "shared toggle")} handler {f.ObjectName}");
        }

        RewriteToggleHandler(sharedToggle, false);
        RewriteToggleHandler(master, true);

        {
            var f = sharedDropdown;
            if (!f.LoadedProperties.Any(p => p.Name.ToString() == "Index"))
                throw new Exception($"{f.ObjectName}: params are [{string.Join(", ", f.LoadedProperties.Select(p => p.Name))}]");
            var ret = f.ScriptBytecode.First(e => e is EX_Return);
            var end = f.ScriptBytecode.First(e => e is EX_EndOfScript);
            var body = StoreWrite(f, () => PVar(f, "Index"), dropdown: true);
            body.Add(("gend", ret, null));
            body.Add((null, end, null));
            SetBody(f, body);
            Console.WriteLine($"  rewrote shared dropdown handler {f.ObjectName}");
        }

        // ---- the builder: RefreshFromSettings ----
        {
            var f = Fn("RefreshFromSettings");
            int fi = page.Exports.IndexOf(f);
            KismetPropertyPointer LP(string n) => Ptr(page, FName.FromString(page, n), new FPackageIndex(fi + 1));
            var objL = AddLocal(f, ObjProp(page, "ZSDM_Obj", EnsureNativeClassImport(page, "/Script/Engine", "SaveGame")));
            var mapL = AddLocal(f, MapStrStrProp(page, "ZSDM_Map"));
            var cdoL = AddLocal(f, ObjProp(page, "ZSDM_CDO", clsObject));
            var rowL = AddLocal(f, ObjProp(page, "ZSDM_Row", clsObject));
            var keysL = AddLocal(f, StrArrayProp(page, "ZSDM_Keys"));
            var labelsL = AddLocal(f, StrArrayProp(page, "ZSDM_Labels"));
            var tipsL = AddLocal(f, StrArrayProp(page, "ZSDM_Tips"));
            var defsL = AddLocal(f, StrArrayProp(page, "ZSDM_Defs"));
            var typesL = AddLocal(f, IntArrayProp(page, "ZSDM_Types"));
            var optsL = AddLocal(f, StrArrayProp(page, "ZSDM_Opts"));
            var textArrL = AddLocal(f, TextArrayProp(page, "ZSDM_OnOff"));
            var iL = AddLocal(f, IntProp(page, "ZSDM_I"));
            var nL = AddLocal(f, IntProp(page, "ZSDM_N"));
            var keyL = AddLocal(f, StrProp(page, "ZSDM_Key"));
            var strL = AddLocal(f, StrProp(page, "ZSDM_Str"));
            var valSL = AddLocal(f, StrProp(page, "ZSDM_ValS"));
            var valL = AddLocal(f, IntProp(page, "ZSDM_Val"));
            var typeL = AddLocal(f, IntProp(page, "ZSDM_Type"));
            var nameL = AddLocal(f, NameProp(page, "ZSDM_Name"));
            var txtL = AddLocal(f, TextProp(page, "ZSDM_Txt"));
            var optSelL = AddLocal(f, StrProp(page, "ZSDM_OptSel"));
            var foundL = AddLocal(f, BoolProp(page, "ZSDM_Found"));
            var bL = AddLocal(f, BoolProp(page, "ZSDM_B"));
            var delTL = AddLocal(f, DelegateProp(page, "ZSDM_DelT", sigToggle));
            var delDL = AddLocal(f, DelegateProp(page, "ZSDM_DelD", sigDrop));

            var body = new List<(string?, KismetExpression, string?)>();
            void S(KismetExpression e) => body.Add((null, e, null));
            void L(string label, KismetExpression e) => body.Add((label, e, null));
            void J(KismetExpression e, string to) => body.Add((null, e, to));
            KismetExpression LetS(string local, Func<KismetExpression> lv, KismetExpression v) =>
                new EX_Let { Value = LP(local), Variable = lv(), Expression = v };

            // store object + map
            S(new EX_LetObj { VariableExpression = objL(), AssignmentExpression = Call(fnLoad, Str(SlotName), Int(0)) });
            S(new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnIsValid, objL()) });
            J(new EX_JumpIfNot { BooleanExpression = bL() }, "mkstore");
            J(new EX_Jump(), "havestore");
            L("mkstore", new EX_LetObj { VariableExpression = objL(), AssignmentExpression = Call(fnCreate, new EX_ObjectConst { Value = saveCls }) });
            L("havestore", LetS("ZSDM_Map", mapL, ReadMember(page, objL(), mapProp, saveCls)));
            // shared disabled/enabled FText pair
            S(LibCall(arrLibObj, fnArrClear, textArrL()));
            S(LetS("ZSDM_Txt", txtL, Call(fnS2T, Str("disabled"))));
            S(LibCall(arrLibObj, fnArrAdd, textArrL(), txtL()));
            S(LetS("ZSDM_Txt", txtL, Call(fnS2T, Str("enabled"))));
            S(LibCall(arrLibObj, fnArrAdd, textArrL(), txtL()));

            for (int k = 0; k < SlotCount; k++)
            {
                string kk = $"s{k}_";
                var (section, header, container) = Sections[k];
                var slotConst = () => new EX_ObjectConst { Value = slotCls[k] };
                FName Reg(string n) => FName.FromString(page, n);

                S(new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnIsValid, slotConst()) });
                J(new EX_JumpIfNot { BooleanExpression = bL() }, kk + "absent");
                // present: show section, set header, read registration
                S(VCallOn(page, PageVar(section), "SetVisibility", new EX_ByteConst { Value = 4 }));
                S(new EX_LetObj { VariableExpression = cdoL(), AssignmentExpression = Call(fnGCDO, slotConst()) });
                S(LetS("ZSDM_Str", strL, ReadMember(page, cdoL(), Reg("ZSREG_Name"), slotCls[k])));
                S(LetS("ZSDM_Txt", txtL, Call(fnS2T, strL())));
                S(VCallOn(page, PageVar(header), "SetText", txtL()));
                S(LetS("ZSDM_Keys", keysL, ReadMember(page, cdoL(), Reg("ZSREG_Keys"), slotCls[k])));
                S(LetS("ZSDM_Labels", labelsL, ReadMember(page, cdoL(), Reg("ZSREG_Labels"), slotCls[k])));
                S(LetS("ZSDM_Tips", tipsL, ReadMember(page, cdoL(), Reg("ZSREG_Tips"), slotCls[k])));
                S(LetS("ZSDM_Defs", defsL, ReadMember(page, cdoL(), Reg("ZSREG_Defaults"), slotCls[k])));
                S(LetS("ZSDM_Types", typesL, ReadMember(page, cdoL(), Reg("ZSREG_Types"), slotCls[k])));
                S(LetS("ZSDM_Opts", optsL, ReadMember(page, cdoL(), Reg("ZSREG_Options"), slotCls[k])));
                S(CallOn(page, PageVar(container), fnClearKids));
                S(LetS("ZSDM_N", nL, LibCall(arrLibObj, fnArrLen, keysL())));
                // empty registration (placeholder slot) -> treat as absent
                S(new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnGreater, nL(), Int(0)) });
                J(new EX_JumpIfNot { BooleanExpression = bL() }, kk + "absent");
                S(LetS("ZSDM_I", iL, Int(0)));
                // per-setting loop
                L(kk + "loop", new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnLess, iL(), nL()) });
                J(new EX_JumpIfNot { BooleanExpression = bL() }, kk + "endloop");
                S(LibCall(arrLibObj, fnArrGet, keysL(), iL(), keyL()));
                S(LibCall(arrLibObj, fnArrGet, typesL(), iL(), typeL()));
                // current value: default unless the map has the key
                S(LibCall(arrLibObj, fnArrGet, defsL(), iL(), valSL()));
                S(new EX_LetBool { VariableExpression = foundL(), AssignmentExpression = LibCall(mapLibObj, fnMapFind, mapL(), keyL(), strL()) });
                J(new EX_JumpIfNot { BooleanExpression = foundL() }, kk + "defval");
                S(LetS("ZSDM_ValS", valSL, strL()));
                L(kk + "defval", LetS("ZSDM_Val", valL, Call(fnS2I, valSL())));
                // toggle or dropdown?
                S(new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnEq, typeL(), Int(1)) });
                J(new EX_JumpIfNot { BooleanExpression = bL() }, kk + "toggle");
                // ---- dropdown row ----
                S(new EX_LetObj { VariableExpression = rowL(), AssignmentExpression = LibCall(wblObj, fnWCreate, new EX_Self(), new EX_ObjectConst { Value = dropdownCls }, new EX_NoObject()) });
                S(LetS("ZSDM_Name", nameL, Call(fnS2Name, keyL())));
                S(Call(fnSetNameP, rowL(), new EX_NameConst { Value = Reg("SettingName") }, nameL()));
                S(LibCall(arrLibObj, fnArrGet, labelsL(), iL(), strL()));
                S(LetS("ZSDM_Txt", txtL, Call(fnS2T, strL())));
                S(Call(fnSetTextP, rowL(), new EX_NameConst { Value = Reg("SettingDisplayName") }, txtL()));
                S(LibCall(arrLibObj, fnArrGet, tipsL(), iL(), strL()));
                S(LetS("ZSDM_Txt", txtL, Call(fnS2T, strL())));
                S(Call(fnSetTextP, rowL(), new EX_NameConst { Value = Reg("SettingDisplayDescription") }, txtL()));
                S(LibCall(arrLibObj, fnArrGet, optsL(), valL(), optSelL()));
                S(CallOn(page, PageVar(container), fnAddChild, rowL()));
                S(LCallOn(page, rowL(), "SetDropdownOptions", optsL(), optSelL()));
                S(new EX_BindDelegate { FunctionName = sharedDropdown.ObjectName, Delegate = delDL(), ObjectTerm = new EX_Self() });
                S(AddMulticast(page, rowL(), "SettingSelected", dropdownCls, delDL()));
                J(new EX_Jump(), kk + "step");
                // ---- toggle row ----
                L(kk + "toggle", new EX_LetObj { VariableExpression = rowL(), AssignmentExpression = LibCall(wblObj, fnWCreate, new EX_Self(), new EX_ObjectConst { Value = discreteCls }, new EX_NoObject()) });
                S(LetS("ZSDM_Name", nameL, Call(fnS2Name, keyL())));
                S(Call(fnSetNameP, rowL(), new EX_NameConst { Value = Reg("SettingName") }, nameL()));
                S(LibCall(arrLibObj, fnArrGet, labelsL(), iL(), strL()));
                S(LetS("ZSDM_Txt", txtL, Call(fnS2T, strL())));
                S(Call(fnSetTextP, rowL(), new EX_NameConst { Value = Reg("SettingDisplayName") }, txtL()));
                S(LibCall(arrLibObj, fnArrGet, tipsL(), iL(), strL()));
                S(LetS("ZSDM_Txt", txtL, Call(fnS2T, strL())));
                S(Call(fnSetTextP, rowL(), new EX_NameConst { Value = Reg("SettingDisplayDescription") }, txtL()));
                S(LibCall(arrLibObj, fnSetArrP, rowL(), new EX_NameConst { Value = Reg("SettingValueDisplayNames") }, textArrL()));
                S(Call(fnSetIntP, rowL(), new EX_NameConst { Value = Reg("SettingValue") }, valL()));
                S(CallOn(page, PageVar(container), fnAddChild, rowL()));
                // master (i == 0) binds the section's master handler, others the shared one
                S(new EX_LetBool { VariableExpression = bL(), AssignmentExpression = Call(fnEq, iL(), Int(0)) });
                J(new EX_JumpIfNot { BooleanExpression = bL() }, kk + "notmaster");
                S(new EX_BindDelegate { FunctionName = master.ObjectName, Delegate = delTL(), ObjectTerm = new EX_Self() });
                J(new EX_Jump(), kk + "bound");
                L(kk + "notmaster", new EX_BindDelegate { FunctionName = sharedToggle.ObjectName, Delegate = delTL(), ObjectTerm = new EX_Self() });
                L(kk + "bound", AddMulticast(page, rowL(), "SettingChanged", discreteCls, delTL()));
                // step
                L(kk + "step", LetS("ZSDM_I", iL, Call(fnAdd, iL(), Int(1))));
                J(new EX_Jump(), kk + "loop");
                // initial graying: call the master handler with the current master value
                L(kk + "endloop", LibCall(arrLibObj, fnArrGet, keysL(), Int(0), keyL()));
                S(LibCall(arrLibObj, fnArrGet, defsL(), Int(0), valSL()));
                S(new EX_LetBool { VariableExpression = foundL(), AssignmentExpression = LibCall(mapLibObj, fnMapFind, mapL(), keyL(), strL()) });
                J(new EX_JumpIfNot { BooleanExpression = foundL() }, kk + "mdef");
                S(LetS("ZSDM_ValS", valSL, strL()));
                L(kk + "mdef", LetS("ZSDM_Val", valL, Call(fnS2I, valSL())));
                S(LetS("ZSDM_Name", nameL, Call(fnS2Name, keyL())));
                S(new EX_LocalVirtualFunction { VirtualFunctionName = master.ObjectName, Parameters = new KismetExpression[] { nameL(), valL() } });
                J(new EX_Jump(), kk + "next");
                // absent: collapse the section
                L(kk + "absent", VCallOn(page, PageVar(section), "SetVisibility", new EX_ByteConst { Value = 1 }));
                L(kk + "next", new EX_Nothing());
            }

            var origRet = f.ScriptBytecode.First(e => e is EX_Return);
            var origEnd = f.ScriptBytecode.First(e => e is EX_EndOfScript);
            L("ret", origRet);
            S(origEnd);
            SetBody(f, body);
            AuditProps(page, f.LoadedProperties.Where(p => p.Name.ToString().StartsWith("ZSDM_")), "builder locals");
            Console.WriteLine($"  rewrote RefreshFromSettings (dynamic builder, {body.Count} stmts, {SlotCount} mods)");
        }

        // vanilla functions that would fight the page at runtime
        foreach (var dead in new[] { "UpdateAdvancedVisibility", "HandleAdvancedToggle", "RefreshLanguageRow" })
        {
            var df = Fn(dead);
            var dret = df.ScriptBytecode.First(e => e is EX_Return);
            var dend = df.ScriptBytecode.First(e => e is EX_EndOfScript);
            df.ScriptBytecode = new[] { dret, dend };
        }
        Console.WriteLine("  neutralized UpdateAdvancedVisibility / HandleAdvancedToggle / RefreshLanguageRow");

        // ---- write + verify ----
        var outPath = Path.Combine(ModsOut, "W_SettingsMenu_Mods.uasset");
        page.Write(outPath);
        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        Console.WriteLine($"  reloaded OK: {check.Exports.Count} exports");

        // dangling-jump scan on every rewritten function
        KismetSerializer.asset = check;
        var rewrittenNames = new HashSet<string> { "RefreshFromSettings", "UpdateAdvancedVisibility", "HandleAdvancedToggle", "RefreshLanguageRow",
            sharedToggle.ObjectName.ToString(), sharedDropdown.ObjectName.ToString() };
        rewrittenNames.Add(master.ObjectName.ToString());
        foreach (var name in rewrittenNames)
        {
            var cf = check.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == name);
            var offs = new HashSet<uint>();
            uint o = 0;
            foreach (var e in cf.ScriptBytecode) { offs.Add(o); o += (uint)Measure(e); }
            void Scan(KismetExpression e)
            {
                switch (e)
                {
                    case EX_Jump j when !offs.Contains(j.CodeOffset): throw new Exception($"{name}: dangling jump {j.CodeOffset}");
                    case EX_JumpIfNot jn when !offs.Contains(jn.CodeOffset): throw new Exception($"{name}: dangling jumpifnot {jn.CodeOffset}");
                }
            }
            foreach (var e in cf.ScriptBytecode) Scan(e);
        }
        Console.WriteLine("  dangling-jump scan: OK (all rewritten functions)");

        // untouched functions byte-identical vs donor
        var vanilla = new UAsset(Path.Combine(RawDir, "W_SettingsMenu_General.uasset"), EngineVersion.VER_UE4_27);
        foreach (var vf in vanilla.Exports.OfType<FunctionExport>())
        {
            var name = vf.ObjectName.ToString().Replace("W_SettingsMenu_General", "W_SettingsMenu_Mods");
            if (rewrittenNames.Contains(name)) continue;
            var pf = check.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == name);
            KismetSerializer.asset = vanilla;
            var va = KismetSerializer.SerializeScript(vf.ScriptBytecode).ToString();
            KismetSerializer.asset = check;
            var pb = KismetSerializer.SerializeScript(pf.ScriptBytecode).ToString()
                .Replace("W_SettingsMenu_Mods", "W_SettingsMenu_General")
                .Replace(PagePkgPath.Replace("/Game/", "Zerospace/Content/"), "Zerospace/Content/Nova/UI/Options/W_SettingsMenu_General");
            if (va != pb) throw new Exception($"untouched function changed: {name}");
        }
        Console.WriteLine("  untouched functions verified identical");
        KismetSerializer.asset = page;
    }

    // ---------- stage 4: containers (Mods tab) ----------

    static void PatchContainers()
    {
        foreach (var name in new[] { "W_OptionsMenu", "W_OptionsMenu_ZS" })
        {
            var asset = new UAsset(Path.Combine(RawDir, name + ".uasset"), EngineVersion.VER_UE4_27);
            if (!asset.VerifyBinaryEquality()) throw new Exception($"{name}: round-trip not binary-equal");

            var pkg = EnsurePackageImport(asset, PagePkgPath);
            var pageCls = AddImport(asset, "/Script/UMG", "WidgetBlueprintGeneratedClass", pkg, PageClsName);

            var tabList = asset.Exports.OfType<NormalExport>().First(e => e.ObjectName.ToString() == "ITL_OptionsTabs");
            var arr = tabList.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "PreregisteredTabInfoArray");
            if (arr.Value.Cast<StructPropertyData>().Any(s => FieldName(s, "TabId") == "Mods"))
                throw new Exception($"{name}: source already has a Mods tab");

            var template = arr.Value.Cast<StructPropertyData>().First(s => FieldName(s, "TabId") == "General");
            var entry = (StructPropertyData)template.Clone();
            entry.Value.OfType<NamePropertyData>().First(x => x.Name.ToString() == "TabId").Value = FName.FromString(asset, "Mods");
            var text = entry.Value.OfType<TextPropertyData>().First(x => x.Name.ToString() == "TabText");
            text.Value = new FString("ZSModManager_ModsTab");
            text.CultureInvariantString = new FString("Mods");
            entry.Value.OfType<ObjectPropertyData>().First(x => x.Name.ToString() == "TabContentType").Value = pageCls;
            arr.Value = arr.Value.Append(entry).ToArray();

            var outPath = Path.Combine(OptionsOut, name + ".uasset");
            asset.Write(outPath);
            var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
            var carr = ((NormalExport)check.Exports.First(e => e.ObjectName.ToString() == "ITL_OptionsTabs"))
                .Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "PreregisteredTabInfoArray");
            var last = (StructPropertyData)carr.Value[^1];
            if (FieldName(last, "TabId") != "Mods") throw new Exception($"{name}: verification failed");
            Console.WriteLine($"{name}: {carr.Value.Length} tabs, last -> Mods");
        }
    }

    static string FieldName(StructPropertyData s, string field) =>
        s.Value.OfType<NamePropertyData>().First(x => x.Name.ToString() == field).Value.ToString();
}
