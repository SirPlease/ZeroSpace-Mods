// Every FPackageIndex must resolve or the game's linker asserts on load (Linker.h:112), and
// UAssetAPI writes a broken one happily. Checks exports, properties and bytecode.

using System.Collections;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.UnrealTypes;

namespace ZSPatchKit;

public static class Indices
{
    /// Report every index that cannot resolve. Nulls are left to the caller, since some
    /// fields legitimately hold one.
    public static List<string> Validate(UAsset asset, string tag)
    {
        var problems = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void CheckIndex(FPackageIndex? ix, string where)
        {
            if (ix == null || ix.IsNull()) return;             // null-ness is a separate check
            if (ix.IsImport())
            {
                int i = -ix.Index - 1;
                if (i < 0 || i >= asset.Imports.Count)
                    problems.Add($"{tag}: {where} -> import #{i}, but the asset has {asset.Imports.Count}");
            }
            else if (ix.IsExport())
            {
                int i = ix.Index - 1;
                if (i < 0 || i >= asset.Exports.Count)
                    problems.Add($"{tag}: {where} -> export #{i}, but the asset has {asset.Exports.Count}");
            }
        }

        void Walk(object? node, string where, int depth)
        {
            if (node == null || depth > 40) return;
            if (node is string || node.GetType().IsPrimitive || node.GetType().IsEnum) return;
            if (node is FPackageIndex fpi) { CheckIndex(fpi, where); return; }
            if (node is FName) return;
            if (!node.GetType().IsValueType && !seen.Add(node)) return;

            if (node is IEnumerable en and not string)
            {
                int n = 0;
                foreach (var item in en) Walk(item, $"{where}[{n++}]", depth + 1);
                return;
            }
            foreach (var f in node.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                Walk(f.GetValue(node), $"{where}.{f.Name}", depth + 1);
            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v;
                try { v = p.GetValue(node); } catch { continue; }
                Walk(v, $"{where}.{p.Name}", depth + 1);
            }
        }

        for (int e = 0; e < asset.Exports.Count; e++)
        {
            var ex = asset.Exports[e];
            string where = $"export '{ex.ObjectName}'";
            CheckIndex(ex.ClassIndex, where + ".ClassIndex");
            CheckIndex(ex.OuterIndex, where + ".OuterIndex");
            CheckIndex(ex.TemplateIndex, where + ".TemplateIndex");
            CheckIndex(ex.SuperIndex, where + ".SuperIndex");

            if (ex is StructExport se)
            {
                foreach (var p in se.LoadedProperties ?? Array.Empty<UAssetAPI.FieldTypes.FProperty>())
                    Walk(p, $"{where}.property '{p.Name}'", 0);
                if (se.ScriptBytecode != null)
                    for (int i = 0; i < se.ScriptBytecode.Length; i++)
                        Walk(se.ScriptBytecode[i], $"{where}.bytecode[{i}]", 0);
            }
            if (ex is ClassExport ce && ce.FuncMap != null)
                foreach (var kv in ce.FuncMap)
                    CheckIndex(kv.Value, $"{where}.FuncMap['{kv.Key}']");
        }
        return problems;
    }
}
