// Import-table management, as free functions over a UAsset. ModAsset's instance methods
// wrap these; patchers that hold several assets at once call them directly.

using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace ZSPatchKit;

public static class Imports
{
    /// Negative package index, or 0 for "not present" - the shape the call sites test.
    public static int Find(UAsset asset, string objectName, string className = "")
    {
        for (int i = 0; i < asset.Imports.Count; i++)
            if (asset.Imports[i].ObjectName.ToString() == objectName &&
                (className == "" || asset.Imports[i].ClassName.ToString() == className))
                return -(i + 1);
        return 0;
    }

    public static FPackageIndex Require(UAsset asset, string objectName, string className = "")
    {
        int i = Find(asset, objectName, className);
        if (i == 0) throw new Exception($"import not found: {className} {objectName}");
        return new FPackageIndex(i);
    }

    public static FPackageIndex Add(UAsset asset, string classPackage, string className,
                                    FPackageIndex outer, string objectName)
    {
        asset.Imports.Add(new Import(
            FName.FromString(asset, classPackage), FName.FromString(asset, className),
            outer, FName.FromString(asset, objectName), false));
        Console.WriteLine($"  +import {className} {objectName}");
        return new FPackageIndex(-asset.Imports.Count);
    }

    public static FPackageIndex EnsurePackage(UAsset asset, string pkg)
    {
        int i = Find(asset, pkg, "Package");
        return i != 0 ? new FPackageIndex(i)
            : Add(asset, "/Script/CoreUObject", "Package", new FPackageIndex(0), pkg);
    }

    public static FPackageIndex EnsureClass(UAsset asset, string scriptPkg, string cls)
    {
        int i = Find(asset, cls, "Class");
        return i != 0 ? new FPackageIndex(i)
            : Add(asset, "/Script/CoreUObject", "Class", EnsurePackage(asset, scriptPkg), cls);
    }

    /// The function must really live in the named class: the game leaves an unresolvable
    /// import null and crashes on the call.
    public static FPackageIndex EnsureFn(UAsset asset, string scriptPkg, string owningClass, string fn)
    {
        int i = Find(asset, fn, "Function");
        return i != 0 ? new FPackageIndex(i)
            : Add(asset, "/Script/CoreUObject", "Function", EnsureClass(asset, scriptPkg, owningClass), fn);
    }

    public static FPackageIndex EnsureStruct(UAsset asset, string scriptPkg, string name)
    {
        int i = Find(asset, name, "ScriptStruct");
        return i != 0 ? new FPackageIndex(i)
            : Add(asset, "/Script/CoreUObject", "ScriptStruct", EnsurePackage(asset, scriptPkg), name);
    }

    /// class-default object import, e.g. Default__KismetArrayLibrary
    public static FPackageIndex EnsureDefaultObject(UAsset asset, string scriptPkg, string cls)
    {
        string n = "Default__" + cls;
        int i = Find(asset, n);
        return i != 0 ? new FPackageIndex(i) : Add(asset, scriptPkg, cls, EnsurePackage(asset, scriptPkg), n);
    }

    /// A Blueprint-generated class from another package, e.g. the mod manager's save class.
    public static FPackageIndex EnsureBlueprintClass(UAsset asset, string pkgPath, string clsName)
    {
        int i = Find(asset, clsName);
        return i != 0 ? new FPackageIndex(i)
            : Add(asset, "/Script/Engine", "BlueprintGeneratedClass", EnsurePackage(asset, pkgPath), clsName);
    }

    /// Like EnsureFn, but the class import must already exist, so a wrong owner name fails
    /// here instead of in game.
    public static FPackageIndex AddFunctionUnder(UAsset asset, string owner, string fn)
    {
        int outerIdx = Find(asset, owner, "Class");
        if (outerIdx == 0) throw new Exception("class import not found: " + owner);
        asset.Imports.Add(new Import(
            FName.FromString(asset, "/Script/CoreUObject"),
            FName.FromString(asset, "Function"),
            new FPackageIndex(outerIdx),
            FName.FromString(asset, fn),
            false));
        return new FPackageIndex(-asset.Imports.Count);
    }
}
