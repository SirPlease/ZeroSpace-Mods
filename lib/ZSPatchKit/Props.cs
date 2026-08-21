// Function locals, shaped the way the compiler shapes them: a missing ArrayDim trips
// `ArrayIndex < ArrayDim` at load, a missing RepNotifyFunc leaves a garbage name index.
// Appending is safe - UStruct::Link assigns frame offsets in declaration order.

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.UnrealTypes;

namespace ZSPatchKit;

public static class Props
{
    /// The fields every local needs regardless of type.
    public static FProperty Finish(UAsset asset, FProperty p, string name, string serializedType)
    {
        p.Name = FName.FromString(asset, name);
        p.SerializedType = FName.FromString(asset, serializedType);
        p.Flags = EObjectFlags.RF_Public;
        p.ArrayDim = EArrayDim.TArray;
        p.PropertyFlags = EPropertyFlags.CPF_None;
        p.RepNotifyFunc = FName.FromString(asset, "None");
        p.BlueprintReplicationCondition = ELifetimeCondition.COND_None;
        return p;
    }

    // Element sizes are the engine's, read off locals the compiler itself emitted.
    public static FProperty Int(UAsset a, string name) =>
        Finish(a, new FGenericProperty { ElementSize = 4 }, name, "IntProperty");

    public static FProperty Str(UAsset a, string name) =>
        Finish(a, new FGenericProperty { ElementSize = 16 }, name, "StrProperty");

    public static FProperty Text(UAsset a, string name) =>
        Finish(a, new FGenericProperty { ElementSize = 24 }, name, "TextProperty");

    public static FProperty Bool(UAsset a, string name) =>
        Finish(a, Kis.BoolProp(), name, "BoolProperty");

    /// An object property must name the class it points at, or the linker asserts on load.
    public static FProperty Object(UAsset a, string name, FPackageIndex propertyClass)
    {
        if (propertyClass == null || propertyClass.IsNull())
            throw new Exception($"local '{name}': an ObjectProperty needs a real PropertyClass "
                              + "import; a null one asserts in the game's linker on load");
        return Finish(a, new FObjectProperty { ElementSize = 8, PropertyClass = propertyClass },
                      name, "ObjectProperty");
    }

    /// Append a local once, so re-running a patcher does not stack duplicates.
    public static void AddLocal(FunctionExport f, FProperty prop)
    {
        if (!f.LoadedProperties.Any(p => p.Name.ToString() == prop.Name.ToString()))
            f.LoadedProperties = f.LoadedProperties.Append(prop).ToArray();
    }
}
