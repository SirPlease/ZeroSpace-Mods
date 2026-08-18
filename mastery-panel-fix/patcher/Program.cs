// Mastery Panel Fix
//
// Rewrites part of the planet panel widget (StarSystemWidgetV2) so the mastery numbers
// on it are the real ones.

using System;
using System.Collections.Generic;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

class Program
{
    static UAsset asset = null!;

    const string VanillaAsset = @"tools\raw\ssw_raw\StarSystemWidgetV2.uasset";
    const string OutputRoot   = @"mods\masterypanelfix\pak_build";
    const string AssetSubDir  = @"Zerospace\Content\RTSGameSample\UI\MainMenu\StarSystemDetails";

    static void Main(string[] args)
    {
        var root = RepoRoot();
        var inPath  = args.Length > 0 ? args[0] : System.IO.Path.Combine(root, VanillaAsset);
        var outRoot = args.Length > 1 ? args[1] : System.IO.Path.Combine(root, OutputRoot);
        PatchPlanetPanel(inPath, outRoot);
    }

    // The default paths are relative to the repo, and `dotnet run` starts in the
    // project folder, so climb up until the vanilla asset comes into view.
    static string RepoRoot()
    {
        var dir = System.IO.Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, VanillaAsset))) return dir;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return System.IO.Directory.GetCurrentDirectory();
    }

    // ---------------- bytecode helpers ----------------

    static FPackageIndex AddFunctionImport(string owner, string fn)
    {
        int outerIdx = 0;
        for (int i = 0; i < asset.Imports.Count; i++)
            if (asset.Imports[i].ObjectName.ToString() == owner && asset.Imports[i].ClassName.ToString() == "Class")
                outerIdx = -(i + 1);
        if (outerIdx == 0) throw new Exception("class import not found: " + owner);
        var imp = new Import(
            FName.FromString(asset, "/Script/CoreUObject"),
            FName.FromString(asset, "Function"),
            new FPackageIndex(outerIdx),
            FName.FromString(asset, fn),
            false);
        asset.Imports.Add(imp);
        return new FPackageIndex(-asset.Imports.Count);
    }

    static EX_CallMath Call(FPackageIndex fn, params KismetExpression[] args)
        => new EX_CallMath { StackNode = fn, Parameters = args };

    static EX_StringConst Str(string s) => new EX_StringConst { Value = s };
    static EX_FloatConst Flt(float f) => new EX_FloatConst { Value = f };
    static EX_IntConst Int(int v) => new EX_IntConst { Value = v };

    // how many bytes a statement takes once written out, for placing the new blocks
    static int Measure(KismetExpression e)
    {
        int i = 0;
        KismetSerializer.SerializeExpression(e, ref i, false);
        return i;
    }

    static IEnumerable<KismetExpression> Children(KismetExpression e)
    {
        foreach (var f in e.GetType().GetFields())
        {
            if (typeof(KismetExpression).IsAssignableFrom(f.FieldType))
            {
                if (f.GetValue(e) is KismetExpression c) yield return c;
            }
            else if (f.FieldType == typeof(KismetExpression[]))
            {
                if (f.GetValue(e) is KismetExpression[] arr)
                    foreach (var c in arr) if (c != null) yield return c;
            }
        }
    }

    static IEnumerable<KismetExpression> Walk(KismetExpression e)
    {
        yield return e;
        foreach (var c in Children(e))
            foreach (var d in Walk(c)) yield return d;
    }

    static string ExprJson(KismetExpression e)
    {
        int i = 0;
        return KismetSerializer.SerializeExpression(e, ref i, false).ToString();
    }

    // Some expressions store code offsets inside themselves. Move one and those
    // offsets still point at the old place, which sends the VM somewhere random. The
    // one that matters here is EX_SwitchValue: the level rows are printed through a
    // switch over the four row widgets. That is why the patch changes the statement
    // that builds the switch's argument and leaves the switch where it is.
    static readonly string[] OffsetBearing = {
        "EX_SwitchValue", "EX_Jump", "EX_JumpIfNot", "EX_PushExecutionFlow",
        "EX_PopExecutionFlowIfNot", "EX_ComputedJump", "EX_Skip"
    };

    static void AssertRelocatable(KismetExpression e, string what)
    {
        foreach (var n in Walk(e))
            if (OffsetBearing.Contains(n.GetType().Name))
                throw new Exception($"{what}: cannot relocate expression containing {n.GetType().Name} (absolute code offsets)");
    }

    // Some engine functions take an argument by reference. Those read the address of
    // the last property the VM touched instead of the value that was just worked out,
    // so passing them a nested call makes them read the wrong memory and crash the
    // game. Give them a plain variable and they are fine. Arguments passed by value can
    // be any expression. Strings and text are by reference, ints and bools by value.
    static readonly Dictionary<string, int[]> RefParamFunctions = new()
    {
        { "Conv_TextToString", new[] { 0 } },
        { "Conv_StringToInt",  new[] { 0 } },
        { "RightChop",         new[] { 0 } },   // Count is by-value int
    };

    static void AuditRefArgs()
    {
        var problems = new List<string>();
        void Check(KismetExpression e, string where)
        {
            if (e is EX_CallMath cm && cm.StackNode.Index < 0
                && RefParamFunctions.TryGetValue(asset.Imports[-cm.StackNode.Index - 1].ObjectName.ToString(), out var refIdx))
            {
                foreach (var i in refIdx)
                    if (i < cm.Parameters.Length && !(cm.Parameters[i] is EX_LocalVariable || cm.Parameters[i] is EX_InstanceVariable || cm.Parameters[i] is EX_DefaultVariable))
                        problems.Add($"{where}: {asset.Imports[-cm.StackNode.Index - 1].ObjectName} param {i} is {cm.Parameters[i].GetType().Name} - by-reference parameters require a plain variable");
            }
            foreach (var c in Children(e)) Check(c, where);
        }
        foreach (var fe in asset.Exports.OfType<FunctionExport>())
            foreach (var st in fe.ScriptBytecode)
                Check(st, fe.ObjectName.ToString());
        if (problems.Count > 0)
            throw new Exception("by-reference argument audit failed:\n  " + string.Join("\n  ", problems.Distinct()));
        Console.WriteLine("by-reference argument audit: OK");
    }

    // ---------------- the patch ----------------

    static void PatchPlanetPanel(string inPath, string outRoot)
    {
        var outDir = System.IO.Path.Combine(outRoot, AssetSubDir);
        System.IO.Directory.CreateDirectory(outDir);
        Console.WriteLine("in:  " + inPath);

        asset = new UAsset(inPath, EngineVersion.VER_UE4_27);
        KismetSerializer.asset = asset;

        FPackageIndex FindFnOpt(string name)
        {
            for (int i = 0; i < asset.Imports.Count; i++)
                if (asset.Imports[i].ObjectName.ToString() == name && asset.Imports[i].ClassName.ToString() == "Function")
                    return new FPackageIndex(-(i + 1));
            return new FPackageIndex(0);
        }
        FPackageIndex FindOrAdd(string owner, string name)
        {
            var f = FindFnOpt(name);
            return f.Index != 0 ? f : AddFunctionImport(owner, name);
        }

        // Only import a function that really lives in the class you name it under. If
        // the game cannot find it the call is left null and the game crashes on the
        // spot. Everything below is either already imported by the widget itself or was
        // checked against the shipped game binary. Note there is no Select* helper in
        // here: a normal branch does the same job and needs no import.
        var FTruncFn    = FindOrAdd("KismetMathLibrary", "FTrunc");
        var GeInt       = FindOrAdd("KismetMathLibrary", "GreaterEqual_IntInt");
        var AddInt      = FindOrAdd("KismetMathLibrary", "Add_IntInt");
        var MulInt      = FindOrAdd("KismetMathLibrary", "Multiply_IntInt");
        var DivInt      = FindOrAdd("KismetMathLibrary", "Divide_IntInt");
        var MinInt      = FindOrAdd("KismetMathLibrary", "Min");
        var MaxInt      = FindOrAdd("KismetMathLibrary", "Max");
        var SubInt      = FindOrAdd("KismetMathLibrary", "Subtract_IntInt");
        var GreaterInt  = FindOrAdd("KismetMathLibrary", "Greater_IntInt");
        var I2F         = FindOrAdd("KismetMathLibrary", "Conv_IntToFloat");
        var DivFlt      = FindOrAdd("KismetMathLibrary", "Divide_FloatFloat");
        var FClamp      = FindOrAdd("KismetMathLibrary", "FClamp");
        var B2I         = FindOrAdd("KismetMathLibrary", "Conv_BoolToInt");
        var I2S         = FindOrAdd("KismetStringLibrary", "Conv_IntToString");
        var Concat      = FindOrAdd("KismetStringLibrary", "Concat_StrStr");
        var StrToInt    = FindOrAdd("KismetStringLibrary", "Conv_StringToInt");
        var RightChopFn = FindOrAdd("KismetStringLibrary", "RightChop");
        var S2T         = FindOrAdd("KismetTextLibrary", "Conv_StringToText");
        var T2S         = FindOrAdd("KismetTextLibrary", "Conv_TextToString");

        KismetExpression Cat2(KismetExpression a, KismetExpression b) => Call(Concat, a, b);

        // the name of the function a call is aimed at, virtual or final
        string CallName(KismetExpression e) => e switch
        {
            EX_VirtualFunction v => v.VirtualFunctionName.ToString(),
            EX_FinalFunction ff when ff.StackNode.Index < 0 => asset.Imports[-ff.StackNode.Index - 1].ObjectName.ToString(),
            EX_FinalFunction ff when ff.StackNode.Index > 0 => asset.Exports[ff.StackNode.Index - 1].ObjectName.ToString(),
            _ => ""
        };

        // MasteryLevel, read exactly the way this function already reads it
        KismetExpression FindRankExpr(FunctionExport fn)
        {
            foreach (var st in fn.ScriptBytecode)
                foreach (var e in Walk(st))
                    if (e is EX_CallMath cm && cm.StackNode.Index < 0
                        && asset.Imports[-cm.StackNode.Index - 1].ObjectName.ToString() == "Greater_IntInt"
                        && cm.Parameters.Length == 2 && ExprJson(cm.Parameters[0]).Contains("MasteryLevel"))
                        return cm.Parameters[0];
            throw new Exception(fn.ObjectName + ": MasteryLevel source not found");
        }

        FunctionExport Fn(string name) => name == "__ubergraph"
            ? asset.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString().StartsWith("ExecuteUbergraph"))
            : asset.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == name);

        var menuFunctions = new[] { "RefreshMenu", "UpdateAllMenus", "__ubergraph" };

        // ================= level rows =================
        // The game prints the mastery's per-stack text on all four rows, so a planet
        // that gives +50 vision reads "+50 unit vision range" four times over. Each row
        // now shows what you actually hold at that level. Reaching level n hands you n
        // more stacks, so the total you hold is 1, 3, 6 then 10, which is n(n+1)/2.
        foreach (var fname in menuFunctions)
        {
            var fn = Fn(fname);
            var fnJson = KismetSerializer.SerializeScript(fn.ScriptBytecode);
            var code = fn.ScriptBytecode.ToList();

            // A spare string variable to pull the description apart in. It has to be
            // one the game never reads again afterwards. In the two menu functions
            // CallFunc_ToUpper_ReturnValue is finished with long before the rows, and
            // in the ubergraph Temp_string_Variable is overwritten right before its
            // only read. Check this again if the widget ever changes.
            var scratchName = fname == "__ubergraph" ? "Temp_string_Variable" : "CallFunc_ToUpper_ReturnValue";
            EX_Let? scratchLet = null;
            foreach (var st in fn.ScriptBytecode)
            {
                if (st is EX_Let sl
                    && (sl.Variable as EX_LocalVariable)?.Variable?.New?.Path?.LastOrDefault().ToString() == scratchName)
                { scratchLet = sl; break; }
            }
            if (scratchLet == null) throw new Exception($"{fn.ObjectName}: scratch local {scratchName} has no assigning EX_Let to clone");
            var ScratchVar = scratchLet.Variable;
            AssertRelocatable(ScratchVar, $"{fn.ObjectName} scratch local");

            // A row is a SetText on the Mastery1..Mastery1_3 widget switch whose
            // argument is not a constant (the constant one is the "blank this row"
            // path). This widget has other switches for unrelated lists, so the name
            // is checked too.
            var rows = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i] is EX_Context cx && cx.ObjectExpression is EX_SwitchValue
                    && CallName(cx.ContextExpression) == "SetText"
                    && cx.ContextExpression is EX_VirtualFunction vfx
                    && vfx.Parameters.Length == 1 && !(vfx.Parameters[0] is EX_TextConst)
                    && ExprJson(cx.ObjectExpression).Contains("Mastery1"))
                    rows.Add(i);
            if (rows.Count == 0) { Console.WriteLine($"{fn.ObjectName}: no level rows"); continue; }

            int eos = (int)fnJson[fnJson.Count - 1]!["StatementIndex"]!;
            var rowInfo = rows.Select(i => (idx: i,
                    off: (int)fnJson[i]!["StatementIndex"]!,
                    next: (int)fnJson[i + 1]!["StatementIndex"]!)).ToList();

            var pend = new List<(EX_Jump entry, List<KismetExpression> block)>();
            foreach (var r in rowInfo.OrderByDescending(x => x.idx))
            {
                // The SetText statement stays where it is, because it holds the switch
                // that cannot be moved. What gets replaced is the statement that builds
                // its argument:
                //     CallFunc_Format_ReturnValue = Format("Level {a} {b}", ...)
                // The switch then prints our text without knowing anything changed.
                var rowArg = ((EX_VirtualFunction)((EX_Context)code[r.idx]).ContextExpression).Parameters[0];
                var argName = (rowArg as EX_LocalVariable)?.Variable?.New?.Path?.LastOrDefault().ToString();
                if (argName == null) throw new Exception($"{fn.ObjectName}: row arg is not a local at {r.off}");

                int letIdx = -1;
                KismetExpression? levelVar = null, descVar = null;
                for (int i = r.idx; i >= 0; i--)
                {
                    if (code[i] is EX_Let l2)
                    {
                        var vn = (l2.Variable as EX_LocalVariable)?.Variable?.New?.Path?.LastOrDefault().ToString();
                        if (letIdx < 0 && vn == argName) letIdx = i;
                        if (descVar == null && l2.Expression is EX_Context c3
                            && CallName(c3.ContextExpression) == "GetItemShortDescription")
                            descVar = l2.Variable;
                        if (levelVar == null && l2.Expression is EX_CallMath m3 && m3.StackNode.Index < 0
                            && asset.Imports[-m3.StackNode.Index - 1].ObjectName.ToString() == "Add_IntInt"
                            && m3.Parameters.Length == 2 && m3.Parameters[1] is EX_IntConst ic && ic.Value == 1)
                            levelVar = l2.Variable;
                    }
                    if (letIdx >= 0 && levelVar != null && descVar != null) break;
                }
                if (letIdx < 0 || levelVar == null || descVar == null)
                    throw new Exception($"{fn.ObjectName}: row sources not found at {r.off} " +
                        $"(let={letIdx} level={levelVar != null} desc={descVar != null})");

                int letOff  = (int)fnJson[letIdx]!["StatementIndex"]!;
                int letNext = (int)fnJson[letIdx + 1]!["StatementIndex"]!;

                // Every mastery description looks like "+<number>[%] <words>", so the
                // number can be read back out at run time and multiplied:
                //   S    = Conv_TextToString(description)   kept in the spare variable
                //   n    = Conv_StringToInt(S)              "+4% turret damage." -> 4
                //   rest = RightChop(S, 2 + [n >= 10])      cuts off "+" and the digits
                //   line = "+" + n * stacks + rest
                // S goes in as a plain variable because Conv_StringToInt and RightChop
                // take it by reference (see RefParamFunctions above).
                KismetExpression N() => Call(StrToInt, ScratchVar);
                KismetExpression Rest() =>
                    Call(RightChopFn, ScratchVar,
                        Call(AddInt, Int(2), Call(B2I, Call(GeInt, N(), Int(10)))));
                KismetExpression Scaled(KismetExpression stacks) =>
                    Cat2(Str("+"), Cat2(Call(I2S, Call(MulInt, N(), stacks)), Rest()));
                KismetExpression Tri(Func<KismetExpression> r) =>
                    Call(DivInt, Call(MulInt, r(), Call(AddInt, r(), Int(1))), Int(2));
                KismetExpression RowLine() =>
                    Cat2(Cat2(Cat2(
                        Str("Level "), Call(I2S, levelVar)),
                        Str(":   ")),
                        Scaled(Tri(() => levelVar!)));

                var letStmt = (EX_Let)code[letIdx];
                AssertRelocatable(letStmt.Variable, $"{fn.ObjectName} row target local");
                AssertRelocatable(levelVar, $"{fn.ObjectName} level local");
                AssertRelocatable(descVar, $"{fn.ObjectName} desc local");

                var block = new List<KismetExpression> {
                    new EX_Let {   // put the description somewhere the reads below can use
                        Value = scratchLet.Value, Variable = ScratchVar,
                        Expression = Call(T2S, descVar) },
                    new EX_Let {
                        Value = letStmt.Value, Variable = letStmt.Variable,
                        Expression = Call(S2T, RowLine()) },
                    new EX_Jump { CodeOffset = (uint)letNext },
                };

                int slot = letNext - letOff;
                if (slot < 6) throw new Exception($"{fn.ObjectName}: row slot too small at {letOff}");
                var entry = new EX_Jump();
                var pad = new List<KismetExpression> { entry };
                for (int i = 0; i < slot - 5; i++) pad.Add(new EX_Nothing());
                code.RemoveAt(letIdx);
                code.InsertRange(letIdx, pad);
                pend.Add((entry, block));
                Console.WriteLine($"{fn.ObjectName}: level row @{letOff} (slot {slot})");
            }

            AppendTails(fn, code, pend, eos);
        }

        // ============ header: level, XP numbers, bar, MASTERED ============
        // The three fields the widget has to work with:
        //   MasteryLevel    starts at 0, and counts the wrong thresholds
        //   CurrentLevelXP  the total minus tri(MasteryLevel) * 18000
        //   NextLevelXPMax  a ladder entry looked up by stack count, and -1 once the
        //                   level counter hits 4, which is what makes it say MASTERED
        // Put the first two back together and you get the planet's real total XP, and
        // everything else follows from that:
        //   mastered  NextLevelXPMax == -1          ->  total >= 72000
        //   level     MasteryLevel                  ->  min(4, total/18000)
        //   current   FTrunc(CurrentLevelXP)        ->  XP inside this level (below)
        //   max       FTrunc(NextLevelXPMax)        ->  18000
        //   bar       CurrentLevelXP/NextLevelXPMax ->  clamp(current/18000)
        // The MASTERED branch is the game's own, string and full bar included. All that
        // changes is when it runs. The two numbers stay ints handed to the game's own
        // Format call, so they get grouped the same way as every other number in the
        // game rather than however this code would have written them.
        foreach (var fname in menuFunctions)
        {
            var fn = Fn(fname);
            var fnJson = KismetSerializer.SerializeScript(fn.ScriptBytecode);
            var code = fn.ScriptBytecode.ToList();
            int eos = (int)fnJson[fnJson.Count - 1]!["StatementIndex"]!;
            var rankExpr = FindRankExpr(fn);
            AssertRelocatable(rankExpr, $"{fn.ObjectName} header rank source");

            KismetExpression? curSrc = null;
            foreach (var st in fn.ScriptBytecode)
                foreach (var e in Walk(st))
                    if (curSrc == null && e is EX_CallMath ccm && ccm.StackNode.Index < 0
                        && asset.Imports[-ccm.StackNode.Index - 1].ObjectName.ToString() == "FTrunc"
                        && ExprJson(ccm).Contains("CurrentLevelXP"))
                        curSrc = ccm.Parameters[0];
            if (curSrc == null) throw new Exception(fn.ObjectName + ": no CurrentLevelXP read found");
            AssertRelocatable(curSrc, $"{fn.ObjectName} CurrentLevelXP source");

            KismetExpression TriL() =>
                Call(DivInt, Call(MulInt, rankExpr, Call(AddInt, rankExpr, Int(1))), Int(2));
            KismetExpression TotalXP() =>
                Call(AddInt, Call(FTruncFn, curSrc), Call(MulInt, TriL(), Int(18000)));
            KismetExpression TrueRank() =>
                Call(MinInt, Int(4), Call(DivInt, TotalXP(), Int(18000)));
            // XP inside the current level is x mod 18000, written out as
            // x - (x/18000)*18000 since there is no modulo call. Every threshold is a
            // multiple of 18,000, so this gives the right answer whether the field holds
            // the planet's total or just the part inside the level. Max(x,0) keeps the
            // -1 out of the sum.
            KismetExpression WindowXP(KismetExpression srcRead)
            {
                KismetExpression X() => Call(MaxInt, Call(FTruncFn, srcRead), Int(0));
                return Call(SubInt, X(), Call(MulInt, Call(DivInt, X(), Int(18000)), Int(18000)));
            }

            var hsites = new List<(int idx, string kind)>();
            for (int i = 0; i < code.Count; i++)
            {
                var hExprSrc = code[i] switch
                {
                    EX_Let l => l.Expression,
                    EX_LetBool lb => lb.AssignmentExpression,
                    _ => null
                };
                if (hExprSrc is EX_CallMath hcm && hcm.StackNode.Index < 0)
                {
                    var hfn = asset.Imports[-hcm.StackNode.Index - 1].ObjectName.ToString();
                    var ej = ExprJson(hcm);
                    if (hfn == "FTrunc" && ej.Contains("NextLevelXPMax")) hsites.Add((i, "max"));
                    else if (hfn == "FTrunc" && ej.Contains("CurrentLevelXP")) hsites.Add((i, "cur"));
                    else if (hfn == "Divide_FloatFloat" && ej.Contains("CurrentLevelXP") && ej.Contains("NextLevelXPMax"))
                        hsites.Add((i, "bar"));
                    else if (hfn == "EqualEqual_FloatFloat" && ej.Contains("NextLevelXPMax"))
                        hsites.Add((i, "maxed"));
                }
                // the {level} slot in the text: the game feeds it MasteryLevel as is
                else if (code[i] is EX_Let lvlLet
                         && ExprJson(lvlLet.Expression).Contains("MasteryLevel")
                         && ExprJson(lvlLet.Variable).Contains("ArgumentValueInt"))
                    hsites.Add((i, "level"));
            }
            if (hsites.Count == 0) { Console.WriteLine($"{fn.ObjectName}: no header sites"); continue; }

            var hpend = new List<(EX_Jump entry, List<KismetExpression> block)>();
            foreach (var s in hsites.OrderByDescending(x => x.idx))
            {
                int off  = (int)fnJson[s.idx]!["StatementIndex"]!;
                int next = (int)fnJson[s.idx + 1]!["StatementIndex"]!;
                var oldStmt = code[s.idx];
                var (oldVar, oldExpr) = oldStmt switch
                {
                    EX_Let l => (l.Variable, l.Expression),
                    EX_LetBool lb => (lb.VariableExpression, lb.AssignmentExpression),
                    _ => throw new Exception($"{fn.ObjectName}: unexpected header stmt {oldStmt.GetType().Name}")
                };
                AssertRelocatable(oldVar, $"{fn.ObjectName} header {s.kind} local");
                KismetExpression? srcRead = (oldExpr is EX_CallMath oce) ? oce.Parameters[0] : null;
                if (srcRead != null) AssertRelocatable(srcRead, $"{fn.ObjectName} header {s.kind} source");
                KismetExpression expr = s.kind switch
                {
                    "cur"   => WindowXP(srcRead!),
                    "max"   => Int(18000),
                    "bar"   => Call(FClamp,
                                   Call(DivFlt, Call(I2F, WindowXP(srcRead!)), Flt(18000f)),
                                   Flt(0f), Flt(1f)),
                    "maxed" => Call(GeInt, TotalXP(), Int(72000)),
                    "level" => TrueRank(),
                    _ => throw new Exception("unreachable")
                };
                KismetExpression newStmt = oldStmt is EX_LetBool
                    ? new EX_LetBool { VariableExpression = oldVar, AssignmentExpression = expr }
                    : new EX_Let { Value = ((EX_Let)oldStmt).Value, Variable = oldVar, Expression = expr };

                var block = new List<KismetExpression> { newStmt, new EX_Jump { CodeOffset = (uint)next } };
                int slot = next - off;
                if (slot < 6) throw new Exception($"{fn.ObjectName}: header slot too small at {off}");
                var entry = new EX_Jump();
                var pad = new List<KismetExpression> { entry };
                for (int i = 0; i < slot - 5; i++) pad.Add(new EX_Nothing());
                code.RemoveAt(s.idx);
                code.InsertRange(s.idx, pad);
                hpend.Add((entry, block));
                Console.WriteLine($"{fn.ObjectName}: header {s.kind} @{off} (slot {slot})");
            }

            AppendTails(fn, code, hpend, eos);
        }

        // ================= row colour =================
        // Each row is drawn white or greyed out by
        //     Array_Get(PersistentData.MasteryLevels, i, out item)
        //     if !(item.Unlocked) goto <grey>
        // and Unlocked is filled in from the same wrong level count, so a mastered
        // planet still has grey rows. The test itself cannot be rewritten in place:
        // it stores a code offset and there is no room in it. So the Array_Get in front
        // of it is redirected instead. The new block runs it, sets item.Unlocked to
        // (real level > i), and jumps back into the game's own test.
        foreach (var fname in menuFunctions)
        {
            var fn = Fn(fname);
            var fnJson = KismetSerializer.SerializeScript(fn.ScriptBytecode);
            var code = fn.ScriptBytecode.ToList();
            int eos = (int)fnJson[fnJson.Count - 1]!["StatementIndex"]!;
            var rankExpr = FindRankExpr(fn);

            KismetExpression? curSrc = null;
            foreach (var st in fn.ScriptBytecode)
                foreach (var e in Walk(st))
                    if (curSrc == null && e is EX_CallMath ccm2 && ccm2.StackNode.Index < 0
                        && asset.Imports[-ccm2.StackNode.Index - 1].ObjectName.ToString() == "FTrunc"
                        && ExprJson(ccm2).Contains("CurrentLevelXP"))
                        curSrc = ccm2.Parameters[0];
            if (curSrc == null) { Console.WriteLine($"{fn.ObjectName}: colour pass, no XP read"); continue; }

            KismetExpression TriC(Func<KismetExpression> r) =>
                Call(DivInt, Call(MulInt, r(), Call(AddInt, r(), Int(1))), Int(2));
            KismetExpression TrueRankC() =>
                Call(MinInt, Int(4),
                    Call(DivInt,
                        Call(AddInt, Call(FTruncFn, curSrc),
                            Call(MulInt, TriC(() => rankExpr), Int(18000))),
                        Int(18000)));

            var csites = new List<(int getIdx, int jifIdx, KismetExpression unlockedRead, KismetExpression idxVar)>();
            for (int i = 1; i < code.Count; i++)
            {
                if (code[i] is EX_JumpIfNot jn && ExprJson(jn.BooleanExpression).Contains("Unlocked")
                    && code[i - 1] is EX_Context gctx && ExprJson(gctx).Contains("MasteryLevels"))
                {
                    // Array_Get's second argument is the loop index
                    var gcall = gctx.ContextExpression;
                    var pars = (gcall as EX_FinalFunction)?.Parameters ?? (gcall as EX_VirtualFunction)?.Parameters;
                    if (pars == null || pars.Length < 2) continue;
                    csites.Add((i - 1, i, jn.BooleanExpression, pars[1]));
                }
            }
            if (csites.Count == 0) { Console.WriteLine($"{fn.ObjectName}: no row-colour sites"); continue; }

            var cpend = new List<(EX_Jump entry, List<KismetExpression> block)>();
            foreach (var s in csites.OrderByDescending(x => x.getIdx))
            {
                int off = (int)fnJson[s.getIdx]!["StatementIndex"]!;
                int next = (int)fnJson[s.jifIdx]!["StatementIndex"]!;
                var getStmt = code[s.getIdx];
                AssertRelocatable(getStmt, $"{fn.ObjectName} Array_Get @{off}");
                AssertRelocatable(s.unlockedRead, $"{fn.ObjectName} Unlocked read @{off}");
                AssertRelocatable(s.idxVar, $"{fn.ObjectName} loop index @{off}");

                var block = new List<KismetExpression> {
                    getStmt,
                    new EX_LetBool {
                        VariableExpression = s.unlockedRead,
                        AssignmentExpression = Call(GreaterInt, TrueRankC(), s.idxVar) },
                    new EX_Jump { CodeOffset = (uint)next },
                };

                int slot = next - off;
                if (slot < 6) throw new Exception($"{fn.ObjectName}: colour slot too small at {off}");
                var entry = new EX_Jump();
                var pad = new List<KismetExpression> { entry };
                for (int i = 0; i < slot - 5; i++) pad.Add(new EX_Nothing());
                code.RemoveAt(s.getIdx);
                code.InsertRange(s.getIdx, pad);
                cpend.Add((entry, block));
                Console.WriteLine($"{fn.ObjectName}: row colour @{off} (slot {slot})");
            }

            AppendTails(fn, code, cpend, eos);
        }

        AuditRefArgs();
        var outPath = System.IO.Path.Combine(outDir, "StarSystemWidgetV2.uasset");
        asset.Write(outPath);
        Console.WriteLine("out: " + outPath);

        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        Console.WriteLine("reloaded exports: " + check.Exports.Count);
    }

    // Put the collected blocks after the function's old end marker, point each jump
    // at its own block, and save the function.
    static void AppendTails(FunctionExport fn, List<KismetExpression> code,
        List<(EX_Jump entry, List<KismetExpression> block)> pending, int endOfScript)
    {
        var tails = new List<KismetExpression>();
        int cur = endOfScript;
        foreach (var (entry, block) in pending)
        {
            entry.CodeOffset = (uint)cur;
            cur += block.Sum(Measure);
            tails.AddRange(block);
        }
        int last = code.Count - 1;
        if (!(code[last] is EX_EndOfScript)) throw new Exception(fn.ObjectName + " does not end with EndOfScript");
        code.InsertRange(last, tails);
        fn.ScriptBytecode = code.ToArray();
    }
}
