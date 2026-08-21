// One vanilla .uasset being patched: load, import management, save, and the checks that
// catch a broken patch before it reaches the game. An instance rather than statics, so a
// patcher can hold more than one asset at a time.

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace ZSPatchKit;

public sealed class ModAsset
{
    public UAsset Asset { get; }
    public string SourcePath { get; }

    private ModAsset(UAsset asset, string path)
    {
        Asset = asset;
        SourcePath = path;
    }

    /// Load a vanilla asset and bind UAssetAPI's serializer to it, which Kis.Measure needs.
    /// Call Bind() again if you interleave work across two assets.
    public static ModAsset Load(string path)
    {
        var a = new UAsset(path, EngineVersion.VER_UE4_27);
        KismetSerializer.asset = a;
        return new ModAsset(a, path);
    }

    /// Adopt an already-loaded UAsset and bind the serializer to it, same as Load.
    public static ModAsset Wrap(UAsset asset, string path = "")
    {
        KismetSerializer.asset = asset;
        return new ModAsset(asset, path);
    }

    public void Bind() => KismetSerializer.asset = Asset;

    // ---------- imports ----------
    // Delegation to Imports.*, so a single-asset patcher can write A.EnsureFn(...).

    public int FindImport(string objectName, string className = "")
        => Imports.Find(Asset, objectName, className);

    public FPackageIndex AddImport(string classPackage, string className, FPackageIndex outer, string objectName)
        => Imports.Add(Asset, classPackage, className, outer, objectName);

    public FPackageIndex EnsurePackage(string pkg) => Imports.EnsurePackage(Asset, pkg);
    public FPackageIndex EnsureClass(string scriptPkg, string cls) => Imports.EnsureClass(Asset, scriptPkg, cls);
    public FPackageIndex EnsureFn(string scriptPkg, string owningClass, string fn)
        => Imports.EnsureFn(Asset, scriptPkg, owningClass, fn);
    public FPackageIndex EnsureStruct(string scriptPkg, string name) => Imports.EnsureStruct(Asset, scriptPkg, name);
    public FPackageIndex EnsureDefaultObject(string scriptPkg, string cls)
        => Imports.EnsureDefaultObject(Asset, scriptPkg, cls);
    public FPackageIndex EnsureBlueprintClass(string pkgPath, string clsName)
        => Imports.EnsureBlueprintClass(Asset, pkgPath, clsName);
    public FPackageIndex AddFunctionImportUnder(string owner, string fn)
        => Imports.AddFunctionUnder(Asset, owner, fn);

    // ---------- functions ----------

    public IEnumerable<FunctionExport> Functions => Asset.Exports.OfType<FunctionExport>();

    public FunctionExport Function(string name) =>
        Functions.FirstOrDefault(f => f.ObjectName.ToString() == name)
        ?? throw new Exception($"function not found: {name}");

    // ---------- verification ----------

    /// Every jump target must land on a statement boundary; a dangling one crashes the game
    /// the moment the function runs.
    public static void ValidateJumps(FunctionExport f, string tag)
    {
        var offs = new HashSet<uint>();
        uint o = 0;
        foreach (var e in f.ScriptBytecode) { offs.Add(o); o += (uint)Kis.Measure(e); }
        foreach (var e in f.ScriptBytecode)
        {
            uint? t = e switch
            {
                EX_Jump j => j.CodeOffset,
                EX_JumpIfNot jn => jn.CodeOffset,
                _ => null,
            };
            if (t.HasValue && !offs.Contains(t.Value))
                throw new Exception($"{tag}: dangling jump {t.Value}");
        }
    }

    /// ClassIndex and TemplateIndex must be non-null: UAssetAPI writes a null one happily,
    /// the game's async loader asserts on it (AsyncLoading.cpp:2955). OuterIndex is skipped,
    /// since a top-level export's outer is legitimately null.
    public void ValidateExports(string tag)
    {
        foreach (var e in Asset.Exports)
        {
            string what = $"{tag}: export '{e.ObjectName}'";
            if (e.ClassIndex == null || e.ClassIndex.IsNull())
                throw new Exception($"{what} has a null ClassIndex");
            if (e.TemplateIndex == null || e.TemplateIndex.IsNull())
                throw new Exception($"{what} has a null TemplateIndex - the game's async "
                                  + "loader asserts on this and will crash on load");
        }
    }

    /// Sweep every property for a null type reference, which the linker asserts on while
    /// loading (Linker.h:112). Reflection rather than a type switch, so property kinds no
    /// patcher has used yet are covered too; FByteProperty.Enum is the one legitimate null.
    public void ValidateProperties(string tag)
    {
        void Check(UAssetAPI.FieldTypes.FProperty p, string where)
        {
            foreach (var f in p.GetType().GetFields())
            {
                if (f.FieldType == typeof(FPackageIndex))
                {
                    if (p.GetType().Name == "FByteProperty" && f.Name == "Enum") continue;
                    if (f.GetValue(p) is not FPackageIndex ix || ix.IsNull())
                        throw new Exception($"{tag}: {where} property '{p.Name}' "
                                          + $"({p.GetType().Name}) has a null {f.Name} - the "
                                          + "game's linker asserts on this while loading");
                }
                else if (typeof(UAssetAPI.FieldTypes.FProperty).IsAssignableFrom(f.FieldType)
                         && f.GetValue(p) is UAssetAPI.FieldTypes.FProperty inner)
                {
                    Check(inner, where);          // array Inner, map Key/Value, set Element
                }
            }
        }

        foreach (var e in Asset.Exports)
        {
            if (e is StructExport se && se.LoadedProperties != null)
                foreach (var p in se.LoadedProperties)
                    Check(p, $"export '{e.ObjectName}'");
        }
    }

    /// Hold a hand-built export against a real one of the same kind and report every field
    /// null in ours that the cooker filled in. Those nulls crash the game on load and pass
    /// every offline check.
    public static List<string> CompareExportShape(Export ours, Export known, string tag)
    {
        var problems = new List<string>();

        void CompareIndices(object a, object b, string where)
        {
            foreach (var f in a.GetType().GetFields())
            {
                if (f.FieldType != typeof(FPackageIndex)) continue;
                bool ourNull = f.GetValue(a) is not FPackageIndex ia || ia.IsNull();
                bool knownNull = f.GetValue(b) is not FPackageIndex ib || ib.IsNull();
                if (ourNull && !knownNull)
                    problems.Add($"{tag}: {where}.{f.Name} is null, but the reference export has one");
            }
            foreach (var pr in a.GetType().GetProperties())
            {
                if (pr.PropertyType != typeof(FPackageIndex) || pr.GetIndexParameters().Length > 0) continue;
                bool ourNull = pr.GetValue(a) is not FPackageIndex ia || ia.IsNull();
                bool knownNull = pr.GetValue(b) is not FPackageIndex ib || ib.IsNull();
                if (ourNull && !knownNull)
                    problems.Add($"{tag}: {where}.{pr.Name} is null, but the reference export has one");
            }
        }

        CompareIndices(ours, known, "export");

        // Every vanilla function export carries 8 trailing zero bytes. Without them the
        // export is 8 bytes short and the loader misreads the next one.
        int ourExtras = ours.Extras?.Length ?? 0, knownExtras = known.Extras?.Length ?? 0;
        if (ourExtras != knownExtras)
            problems.Add($"{tag}: Extras is {ourExtras} bytes, the reference export has {knownExtras}");

        if (ours is StructExport os && known is StructExport ks)
        {
            if (os.Field == null && ks.Field != null) problems.Add($"{tag}: export.Field is null");
            foreach (var (list, name) in new[]
                     {
                         (os.SerializationBeforeSerializationDependencies, "serialization-before-serialization"),
                         (os.CreateBeforeSerializationDependencies, "create-before-serialization"),
                         (os.CreateBeforeCreateDependencies, "create-before-create"),
                     })
                if (list == null) problems.Add($"{tag}: {name} dependency list is null");

            // parameters, matched by name
            var kp = (ks.LoadedProperties ?? Array.Empty<UAssetAPI.FieldTypes.FProperty>())
                     .ToDictionary(p => p.Name.ToString(), p => p);
            foreach (var p in os.LoadedProperties ?? Array.Empty<UAssetAPI.FieldTypes.FProperty>())
            {
                if (!kp.TryGetValue(p.Name.ToString(), out var k)) continue;   // ours only: nothing to compare
                if (p.GetType() != k.GetType())
                    problems.Add($"{tag}: property '{p.Name}' is {p.GetType().Name}, reference has {k.GetType().Name}");
                else
                    CompareIndices(p, k, $"property '{p.Name}'");
                if (p.ElementSize != k.ElementSize)
                    problems.Add($"{tag}: property '{p.Name}' ElementSize {p.ElementSize}, reference has {k.ElementSize}");
                if (p.PropertyFlags != k.PropertyFlags)
                    problems.Add($"{tag}: property '{p.Name}' flags {p.PropertyFlags}, reference has {k.PropertyFlags}");
            }
        }
        return problems;
    }

    /// Write, then reload from disk and re-validate, which proves the bytes on disk parse
    /// back.
    public void WriteAndVerify(string outPath, string tag)
    {
        ValidateExports(tag);
        ValidateProperties(tag);
        var ixProblems = Indices.Validate(Asset, tag);
        if (ixProblems.Count > 0)
            throw new Exception("unresolvable package indices:" + Environment.NewLine
                                + "  " + string.Join(Environment.NewLine + "  ", ixProblems));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        Asset.Write(outPath);

        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        KismetSerializer.asset = check;
        foreach (var cf in check.Exports.OfType<FunctionExport>())
            ValidateJumps(cf, $"{tag}.{cf.ObjectName} (reload)");
        KismetSerializer.asset = Asset;
        Console.WriteLine($"  wrote {outPath}; reload OK");
    }
}
