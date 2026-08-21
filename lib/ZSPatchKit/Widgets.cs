// Copying a widget inside a cooked UMG asset: the widget export, its slot export, the
// parent's Slots array and the class property the bytecode reaches it by. Proven by the mod
// manager, which clones whole settings sections this way (it still carries its own copy of
// this code; migrate it the next time it is touched, gated on a byte-identical rebuild).

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace ZSPatchKit;

public static class Widgets
{
    public static int ExportIdx(UAsset a, Export e) => a.Exports.IndexOf(e) + 1;   // 1-based

    public static NormalExport Widget(UAsset a, string name) =>
        (NormalExport)a.Exports.First(e => e.ObjectName.ToString() == name);

    public static ObjectPropertyData OProp(NormalExport ex, string name) =>
        (ObjectPropertyData)ex.Data.First(d => d.Name.ToString() == name);

    static List<FPackageIndex>[] DepLists(Export e) => new[]
    {
        e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies,
        e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies,
    };

    static void Remap(List<FPackageIndex> deps, int from, int to)
    {
        for (int i = 0; i < deps.Count; i++)
            if (deps[i].Index == from) deps[i] = new FPackageIndex(to);
    }

    /// Copy an export field for field, including the trailing Extras bytes. An export written
    /// without those is short and the loader misreads the next one.
    public static NormalExport CloneExport(UAsset a, NormalExport src, string newName)
    {
        var dst = new NormalExport(a, src.Extras?.ToArray())
        {
            ObjectName = FName.FromString(a, newName),
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
        a.Exports.Add(dst);
        return dst;
    }

    /// Give the class a widget-binding property for a cloned widget, copied from the one the
    /// original widget already has. Without it the bytecode has no name to reach the copy by.
    public static void AddWidgetBinding(UAsset a, ClassExport cls, string srcName, string newName)
    {
        var tpl = cls.LoadedProperties.OfType<FObjectProperty>().First(p => p.Name.ToString() == srcName);
        cls.LoadedProperties = cls.LoadedProperties.Append(new FObjectProperty
        {
            Name = FName.FromString(a, newName),
            SerializedType = tpl.SerializedType,
            Flags = tpl.Flags,
            ArrayDim = tpl.ArrayDim,
            ElementSize = tpl.ElementSize,
            PropertyFlags = tpl.PropertyFlags,
            RepIndex = tpl.RepIndex,
            RepNotifyFunc = FName.FromString(a, "None"),
            BlueprintReplicationCondition = tpl.BlueprintReplicationCondition,
            PropertyClass = tpl.PropertyClass,
        }).ToArray();
    }

    /// Copy a widget together with its slot. The copy keeps the original's parent.
    public static (NormalExport W, NormalExport S) ClonePair(UAsset a, ClassExport cls, NormalExport srcW, string newName)
    {
        int srcWIdx = ExportIdx(a, srcW);
        int srcSIdx = OProp(srcW, "Slot").Value.Index;
        var srcS = (NormalExport)a.Exports[srcSIdx - 1];

        var dstW = CloneExport(a, srcW, newName);
        int dstWIdx = a.Exports.Count;
        var dstS = CloneExport(a, srcS, srcS.ObjectName.ToString() + "_" + newName);
        int dstSIdx = a.Exports.Count;

        OProp(dstW, "Slot").Value = new FPackageIndex(dstSIdx);
        OProp(dstS, "Content").Value = new FPackageIndex(dstWIdx);

        foreach (var lst in DepLists(dstW).Concat(DepLists(dstS)))
        {
            Remap(lst, srcWIdx, dstWIdx);
            Remap(lst, srcSIdx, dstSIdx);
        }
        // whatever depended on the original pair now also depends on the copy
        foreach (var ex in a.Exports)
        {
            if (ReferenceEquals(ex, dstW) || ReferenceEquals(ex, dstS)) continue;
            foreach (var lst in DepLists(ex))
            {
                if (lst.Any(x => x.Index == srcWIdx) && !lst.Any(x => x.Index == dstWIdx)) lst.Add(new FPackageIndex(dstWIdx));
                if (lst.Any(x => x.Index == srcSIdx) && !lst.Any(x => x.Index == dstSIdx)) lst.Add(new FPackageIndex(dstSIdx));
            }
        }
        AddWidgetBinding(a, cls, srcW.ObjectName.ToString(), newName);
        return (dstW, dstS);
    }

    /// Put a slot into its parent's Slots array, directly after another slot.
    public static void InsertSlotAfter(UAsset a, NormalExport parent, int afterSlotIdx, int newSlotIdx)
    {
        var slots = (ArrayPropertyData)parent.Data.First(d => d.Name.ToString() == "Slots");
        var tpl = (ObjectPropertyData)((ObjectPropertyData)slots.Value[0]).Clone();
        tpl.Name = FName.FromString(a, "Slots");
        tpl.Value = new FPackageIndex(newSlotIdx);

        var list = slots.Value.ToList();
        int at = list.FindIndex(v => v is ObjectPropertyData o && o.Value.Index == afterSlotIdx);
        if (at < 0) throw new Exception("InsertSlotAfter: slot " + afterSlotIdx + " is not in this parent");
        list.Insert(at + 1, tpl);
        slots.Value = list.ToArray();
    }
}
