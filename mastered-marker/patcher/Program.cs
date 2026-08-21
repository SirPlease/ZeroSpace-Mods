// Mastered Marker
//
// Shows a planet's mastery rank on the galaxy map's hover tooltip, and marks the mastered
// ones. WBP_GalaxyPieTooltip has no Blueprint logic and is filled from C++, so this adds a
// Tick to it: read the title the native code just wrote, find the NovaGalaxyMapIcon with
// that title, read its mastery data.
//
// Rank test matches the planet panel: total = FTrunc(CurrentLevelXP) + tri(MasteryLevel)
// * 18000, four ranks of 18,000.

using System;
using System.Collections.Generic;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using ZSPatchKit;
using static ZSPatchKit.Kis;

class Program
{
    const string DefaultInput = @"mods\masteredmarker\raw\WBP_GalaxyPieTooltip.uasset";
    const string DefaultOutput = @"mods\masteredmarker\pak_build";
    const string AssetSubDir = @"Zerospace\Content\Nova\UI\Galaxy";

    // U+221A RADICAL, not a checkmark: no UI font in the game carries U+2713 or U+2714.
    const string Marker = "  √";
    const string LevelTag = "   [Level: ";

    static ModAsset A = null!;
    static UAsset asset => A.Asset;

    static void Main(string[] args)
    {
        var root = Repo.Root(DefaultInput);
        var inPath = args.Length > 0 ? args[0] : System.IO.Path.Combine(root, DefaultInput);
        var outRoot = args.Length > 1 ? args[1] : System.IO.Path.Combine(root, DefaultOutput);

        Console.WriteLine("in:  " + inPath);
        A = ModAsset.Load(inPath);
        KismetSerializer.asset = asset;
        if (!asset.VerifyBinaryEquality()) throw new Exception("donor round-trip not binary-equal");

        var cls = asset.Exports.OfType<ClassExport>().Single();
        int clsIdx = asset.Exports.IndexOf(cls);
        if (asset.Exports.OfType<FunctionExport>().Any())
            throw new Exception("this widget already has functions - the vanilla one has none, "
                              + "so the donor is wrong or the game changed");

        // ---------- imports ----------
        // Static library calls need imports. Member calls are emitted as virtual calls,
        // which resolve by name at run time.
        var fnTickSuper = A.EnsureFn("/Script/UMG", "UserWidget", "Tick");
        var isValid = A.EnsureFn("/Script/Engine", "KismetSystemLibrary", "IsValid");
        var getAll = A.EnsureFn("/Script/Engine", "GameplayStatics", "GetAllActorsOfClass");
        var arrLen = A.EnsureFn("/Script/Engine", "KismetArrayLibrary", "Array_Length");
        var arrGet = A.EnsureFn("/Script/Engine", "KismetArrayLibrary", "Array_Get");
        var arrCdo = A.EnsureDefaultObject("/Script/Engine", "KismetArrayLibrary");
        var t2s = A.EnsureFn("/Script/Engine", "KismetTextLibrary", "Conv_TextToString");
        var s2t = A.EnsureFn("/Script/Engine", "KismetTextLibrary", "Conv_StringToText");
        var concat = A.EnsureFn("/Script/Engine", "KismetStringLibrary", "Concat_StrStr");
        var strEq = A.EnsureFn("/Script/Engine", "KismetStringLibrary", "EqualEqual_StrStr");
        var contains = A.EnsureFn("/Script/Engine", "KismetStringLibrary", "Contains");
        var lessInt = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "Less_IntInt");
        var addInt = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "Add_IntInt");
        var mulInt = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "Multiply_IntInt");
        var divInt = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "Divide_IntInt");
        var geInt = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "GreaterEqual_IntInt");
        var fTrunc = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "FTrunc");
        var minInt = A.EnsureFn("/Script/Engine", "KismetMathLibrary", "Min");
        var i2s = A.EnsureFn("/Script/Engine", "KismetStringLibrary", "Conv_IntToString");
        var linearColor = A.EnsureStruct("/Script/CoreUObject", "LinearColor");
        var slateColor = A.EnsureStruct("/Script/SlateCore", "SlateColor");

        var clsUserWidget = A.EnsureClass("/Script/UMG", "UserWidget");     // Tick's owner
        var clsTextBlock = A.EnsureClass("/Script/UMG", "TextBlock");       // the title widget
        var clsActor = A.EnsureClass("/Script/Engine", "Actor");            // sweep result type
        var clsPie = A.EnsureClass("/Script/RTS", "NovaGalaxyPieTooltip");   // owns TitleText
        var clsIcon = A.EnsureClass("/Script/RTS", "NovaGalaxyMapIcon");    // the map nodes
        var structLoc = A.EnsureStruct("/Script/RTS", "GalacticMapLocation_DataTable");
        var structGeo = A.EnsureStruct("/Script/SlateCore", "Geometry");    // Tick's parameter

        // ---------- the cache variable on the widget class ----------
        // Keeps the actor sweep to once per hover instead of once per frame.
        var lastTitle = Props.Str(asset, "ZSPT_LastTitle");
        cls.LoadedProperties = cls.LoadedProperties.Append(lastTitle).ToArray();
        Console.WriteLine("  + class variable ZSPT_LastTitle");

        // ---------- the Tick function ----------
        // Signature copied off StarSystemWidgetV2's Tick: FGeometry of 56 bytes and a float.
        // ProcessEvent copies the caller's parameter struct through these, so sizes matter.
        const EPropertyFlags parmFlags = EPropertyFlags.CPF_Parm
                                       | EPropertyFlags.CPF_BlueprintVisible
                                       | EPropertyFlags.CPF_BlueprintReadOnly;
        var pGeom = Props.Finish(asset, new FStructProperty { ElementSize = 56, Struct = structGeo },
                                 "MyGeometry", "StructProperty");
        pGeom.PropertyFlags = parmFlags;
        var pDelta = Props.Finish(asset, new FGenericProperty { ElementSize = 4 },
                                  "InDeltaTime", "FloatProperty");
        pDelta.PropertyFlags = parmFlags;

        var locals = new List<FProperty> { pGeom, pDelta };
        var extraLocals = new List<FProperty>
        {
            // An object property must name its class, or the linker asserts (Linker.h:112).
            Props.Object(asset, "ZSPT_Title", clsTextBlock),
            Props.Object(asset, "ZSPT_Icon", clsIcon),
            Props.Bool(asset, "ZSPT_B"),
            Props.Int(asset, "ZSPT_I"),
            Props.Int(asset, "ZSPT_N"),
            Props.Int(asset, "ZSPT_Total"),
            Props.Text(asset, "ZSPT_Txt"),
            Props.Text(asset, "ZSPT_IconTxt"),
            Props.Text(asset, "ZSPT_NewT"),
            Props.Str(asset, "ZSPT_S"),
            Props.Str(asset, "ZSPT_IS"),
            Props.Str(asset, "ZSPT_M"),
            Props.Str(asset, "ZSPT_New"),
            // SlateColor is 40 bytes - taken from WBP_MMOEndScreen's own
            // K2Node_MakeStruct_SlateColor local, not from memory.
            Props.Finish(asset, new FStructProperty { ElementSize = 40, Struct = slateColor },
                         "ZSPT_Col", "StructProperty"),
            Props.Int(asset, "ZSPT_Lvl"),
            Props.Object(asset, "ZSPT_M2", clsTextBlock),
            Props.Str(asset, "ZSPT_S2"),
            Props.Str(asset, "ZSPT_New2"),
            Props.Text(asset, "ZSPT_T2"),
        };
        // GetAllActorsOfClass hands back a TArray<AActor*>, so the inner property is Actor
        var arrInner = Props.Object(asset, "ZSPT_Actors", clsActor);
        var pActors = Props.Finish(asset, new FArrayProperty { ElementSize = 16, Inner = arrInner },
                                   "ZSPT_Actors", "ArrayProperty");
        extraLocals.Add(pActors);
        locals.AddRange(extraLocals);
        Console.WriteLine($"  {locals.Count} function properties");

        int tickIdx = asset.Exports.Count + 1;      // 1-based package index of the new export
        // Every vanilla function export carries 8 trailing zero bytes in Extras. Without
        // them the export is 8 bytes short and the loader misreads the next one, which
        // asserts in the linker (Linker.h:112).
        var tick = new FunctionExport(asset, new byte[8])
        {
            ObjectName = FName.FromString(asset, "Tick"),
            OuterIndex = new FPackageIndex(clsIdx + 1),
            // the widget ships with no functions, so it has no Class'Function' import yet
            ClassIndex = A.EnsureClass("/Script/CoreUObject", "Function"),
            SuperIndex = fnTickSuper,
            // NOT null: the async loader asserts on it (AsyncLoading.cpp:2955). A real
            // Blueprint Tick points at the CDO import Default__Function.
            TemplateIndex = A.EnsureDefaultObject("/Script/CoreUObject", "Function"),
            ObjectFlags = EObjectFlags.RF_Public,
            SuperStruct = fnTickSuper,
            Children = Array.Empty<FPackageIndex>(),
            LoadedProperties = locals.ToArray(),
            FunctionFlags = EFunctionFlags.FUNC_BlueprintCosmetic | EFunctionFlags.FUNC_Event
                          | EFunctionFlags.FUNC_Public | EFunctionFlags.FUNC_BlueprintEvent,
            ScriptBytecode = Array.Empty<KismetExpression>(),
            Data = new List<UAssetAPI.PropertyTypes.Objects.PropertyData>(),
            // The writer walks all of these and a fresh export leaves them null.
            Field = new UAssetAPI.FieldTypes.UField { Next = new FPackageIndex(0) },
            // Preload ordering for the async loader, mirroring StarSystemWidgetV2's Tick:
            // super + parameter struct, this function + parent widget class, owning class
            // + super.
            SerializationBeforeSerializationDependencies = new List<FPackageIndex> { fnTickSuper, structGeo },
            CreateBeforeSerializationDependencies = new List<FPackageIndex> { new FPackageIndex(tickIdx), clsUserWidget },
            SerializationBeforeCreateDependencies = new List<FPackageIndex>(),
            CreateBeforeCreateDependencies = new List<FPackageIndex> { new FPackageIndex(clsIdx + 1), fnTickSuper },
        };
        asset.Exports.Add(tick);
        if (asset.Exports.Count != tickIdx) throw new Exception("tick export index precomputation wrong");

        // ---------- the flag that actually decides whether Tick is called ----------
        // UUserWidget::NativeTick only calls the Blueprint Tick if bHasScriptImplementedTick,
        // which the cooker baked in as false for a widget that shipped without one.
        {
            var cdo = asset.Exports.OfType<NormalExport>()
                           .FirstOrDefault(e => e.ObjectName.ToString().StartsWith("Default__"))
                      ?? throw new Exception("no CDO export");
            var flag = cdo.Data.OfType<UAssetAPI.PropertyTypes.Objects.BoolPropertyData>()
                          .FirstOrDefault(d => d.Name.ToString() == "bHasScriptImplementedTick")
                       ?? throw new Exception("CDO has no bHasScriptImplementedTick to flip");
            if (!flag.Value)
            {
                flag.Value = true;
                Console.WriteLine("  CDO bHasScriptImplementedTick: false -> true");
            }
        }

        var fnPi = new FPackageIndex(tickIdx);
        KismetPropertyPointer L(string n) => Kis.Ptr(FName.FromString(asset, n), fnPi);
        EX_LocalVariable LV(string n) => new EX_LocalVariable { Variable = L(n) };
        KismetPropertyPointer C(string n) => Kis.Ptr(FName.FromString(asset, n), new FPackageIndex(clsIdx + 1));
        EX_InstanceVariable CV(string n) => new EX_InstanceVariable { Variable = C(n) };

        // TitleText and MasteryText are declared by the native parent, so their pointers are
        // owned by the NovaGalaxyPieTooltip import, not by this Blueprint class.
        EX_InstanceVariable MasteryWidget() =>
            new EX_InstanceVariable { Variable = Kis.Ptr(FName.FromString(asset, "MasteryText"), clsPie) };
        EX_InstanceVariable TitleWidget() =>
            new EX_InstanceVariable { Variable = Kis.Ptr(FName.FromString(asset, "TitleText"), clsPie) };

        // obj.<Fn>(args) as a virtual call: resolves by name at run time, needs no import.
        EX_Context VCall(KismetExpression target, string fn, params KismetExpression[] args)
        {
            var call = new EX_VirtualFunction { VirtualFunctionName = FName.FromString(asset, fn), Parameters = args };
            return new EX_Context
            {
                ObjectExpression = target,
                Offset = (uint)Measure(call),
                RValuePointer = Kis.NullPtr(),
                ContextExpression = call,
            };
        }

        // Colour a text block: fill a SlateColor local, then hand it to SetColorAndOpacity.
        IEnumerable<KismetExpression> SetColour(string widgetLocal, float r, float g, float b)
        {
            yield return new EX_Let
            {
                Value = Kis.NullPtr(),
                Variable = new EX_StructMemberContext
                {
                    StructMemberExpression = Kis.Ptr(FName.FromString(asset, "SpecifiedColor"), slateColor),
                    StructExpression = LV("ZSPT_Col"),
                },
                Expression = new EX_StructConst
                {
                    Struct = linearColor,
                    StructSize = 16,
                    Value = new KismetExpression[]
                    {
                        new EX_FloatConst { Value = r }, new EX_FloatConst { Value = g },
                        new EX_FloatConst { Value = b }, new EX_FloatConst { Value = 1.0f },
                    },
                },
            };
            yield return new EX_Let
            {
                Value = Kis.NullPtr(),
                Variable = new EX_StructMemberContext
                {
                    StructMemberExpression = Kis.Ptr(FName.FromString(asset, "ColorUseRule"), slateColor),
                    StructExpression = LV("ZSPT_Col"),
                },
                Expression = Kis.Byte(0),        // UseColor_Specified
            };
            yield return VCall(LV(widgetLocal), "SetColorAndOpacity", LV("ZSPT_Col"));
        }

        // icon.PersistentData.<member>
        KismetExpression IconMember(string name) => new EX_StructMemberContext
        {
            StructMemberExpression = Kis.Ptr(FName.FromString(asset, name), structLoc),
            StructExpression = new EX_Context
            {
                ObjectExpression = LV("ZSPT_Icon"),
                Offset = (uint)Measure(new EX_InstanceVariable { Variable = Kis.Ptr(FName.FromString(asset, "PersistentData"), clsIcon) }),
                RValuePointer = Kis.Ptr(FName.FromString(asset, "PersistentData"), clsIcon),
                ContextExpression = new EX_InstanceVariable { Variable = Kis.Ptr(FName.FromString(asset, "PersistentData"), clsIcon) },
            },
        };
        KismetExpression Rank() => IconMember("MasteryLevel");
        KismetExpression Tri() => Call(divInt, Call(mulInt, Rank(), Call(addInt, Rank(), Int(1))), Int(2));
        KismetExpression Total() => Call(addInt, Call(fTrunc, IconMember("CurrentLevelXP")),
                                                 Call(mulInt, Tri(), Int(18000)));

        // ---------- body ----------
        // Statements with symbolic labels, resolved to byte offsets in one pass. Kismet has
        // no "jump if true", so every early exit is a JumpIfNot over a Jump.
        var jumps = new List<(KismetExpression stmt, string label)>();
        EX_JumpIfNot IfNot(string label)
        {
            var j = new EX_JumpIfNot { BooleanExpression = LV("ZSPT_B") };
            jumps.Add((j, label));
            return j;
        }
        EX_Jump Goto(string label)
        {
            var j = new EX_Jump();
            jumps.Add((j, label));
            return j;
        }
        var labels = new Dictionary<string, int>();
        var body = new List<KismetExpression>();
        void Mark(string label) => labels[label] = body.Count;

        // ZSPT_Title = TitleText;  if it is not there, do nothing
        body.Add(new EX_LetObj { VariableExpression = LV("ZSPT_Title"), AssignmentExpression = TitleWidget() });
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"), AssignmentExpression = Call(isValid, LV("ZSPT_Title")) });
        body.Add(IfNot("RET"));

        // read what the native code just wrote into the title
        body.Add(new EX_Let { Value = L("ZSPT_Txt"), Variable = LV("ZSPT_Txt"),
                              Expression = VCall(LV("ZSPT_Title"), "GetText") });
        body.Add(new EX_Let { Value = L("ZSPT_S"), Variable = LV("ZSPT_S"), Expression = Call(t2s, LV("ZSPT_Txt")) });

        // unchanged since the last tick? then there is nothing to do. This is what keeps the
        // actor sweep to once per hover instead of once per frame.
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(strEq, LV("ZSPT_S"), CV("ZSPT_LastTitle")) });
        body.Add(IfNot("CHANGED"));
        body.Add(Goto("RET"));
        Mark("CHANGED");
        body.Add(new EX_Let { Value = C("ZSPT_LastTitle"), Variable = CV("ZSPT_LastTitle"), Expression = LV("ZSPT_S") });
        // The widget is reused for every planet, so reset the colour before setting it.
        body.AddRange(SetColour("ZSPT_Title", 1.0f, 1.0f, 1.0f));

        // Already carrying the marker? Our own SetText comes back through here. Naming the
        // unicode token explicitly keeps Measure and the writer in step on a non-ASCII value.
        body.Add(new EX_Let { Value = L("ZSPT_M"), Variable = LV("ZSPT_M"),
                              Expression = new EX_UnicodeStringConst { Value = Marker } });
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(contains, LV("ZSPT_S"), LV("ZSPT_M"),
                                                              new EX_False(), new EX_False()) });
        body.Add(IfNot("SWEEP"));
        body.Add(Goto("RET"));

        // find the map icon whose title matches the one on screen
        Mark("SWEEP");
        body.Add(Call(getAll, new EX_Self(), new EX_ObjectConst { Value = clsIcon }, LV("ZSPT_Actors")));
        body.Add(new EX_Let { Value = L("ZSPT_I"), Variable = LV("ZSPT_I"), Expression = Int(0) });
        Mark("LOOP");
        body.Add(new EX_Let { Value = L("ZSPT_N"), Variable = LV("ZSPT_N"),
                              Expression = LibCall(arrCdo, arrLen, LV("ZSPT_Actors")) });
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(lessInt, LV("ZSPT_I"), LV("ZSPT_N")) });
        body.Add(IfNot("RET"));                       // ran out of icons: no match, give up
        body.Add(LibCall(arrCdo, arrGet, LV("ZSPT_Actors"), LV("ZSPT_I"), LV("ZSPT_Icon")));
        body.Add(VCall(LV("ZSPT_Icon"), "GetGalaxyIconTitle", LV("ZSPT_IconTxt")));
        body.Add(new EX_Let { Value = L("ZSPT_IS"), Variable = LV("ZSPT_IS"), Expression = Call(t2s, LV("ZSPT_IconTxt")) });
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(strEq, LV("ZSPT_IS"), LV("ZSPT_S")) });
        body.Add(IfNot("NEXT"));

        // matched: mastered by the same rule the planet panel uses
        body.Add(new EX_Let { Value = L("ZSPT_Total"), Variable = LV("ZSPT_Total"), Expression = Total() });
        // rank = min(4, total / 18000) - the ladder the planet panel uses
        body.Add(new EX_Let { Value = L("ZSPT_Lvl"), Variable = LV("ZSPT_Lvl"),
                              Expression = Call(minInt, Int(4), Call(divInt, LV("ZSPT_Total"), Int(18000))) });

        // ---------------- the mastery line: append the rank, then colour it ----------------
        body.Add(new EX_LetObj { VariableExpression = LV("ZSPT_M2"), AssignmentExpression = MasteryWidget() });
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"), AssignmentExpression = Call(isValid, LV("ZSPT_M2")) });
        body.Add(IfNot("TITLE"));
        body.Add(new EX_Let { Value = L("ZSPT_T2"), Variable = LV("ZSPT_T2"),
                              Expression = VCall(LV("ZSPT_M2"), "GetText") });
        body.Add(new EX_Let { Value = L("ZSPT_S2"), Variable = LV("ZSPT_S2"), Expression = Call(t2s, LV("ZSPT_T2")) });
        // the native fill rewrites this line on every hover, but guard anyway
        body.Add(new EX_Let { Value = L("ZSPT_New2"), Variable = LV("ZSPT_New2"), Expression = Str(LevelTag) });
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(contains, LV("ZSPT_S2"), LV("ZSPT_New2"),
                                                              new EX_False(), new EX_False()) });
        body.Add(IfNot("APPENDLVL"));
        body.Add(Goto("COLOURLINE"));
        Mark("APPENDLVL");
        body.Add(new EX_Let { Value = L("ZSPT_New2"), Variable = LV("ZSPT_New2"),
                              Expression = Call(concat, LV("ZSPT_S2"), LV("ZSPT_New2")) });
        body.Add(new EX_Let { Value = L("ZSPT_S2"), Variable = LV("ZSPT_S2"), Expression = Call(i2s, LV("ZSPT_Lvl")) });
        body.Add(new EX_Let { Value = L("ZSPT_New2"), Variable = LV("ZSPT_New2"),
                              Expression = Call(concat, LV("ZSPT_New2"), LV("ZSPT_S2")) });
        body.Add(new EX_Let { Value = L("ZSPT_S2"), Variable = LV("ZSPT_S2"), Expression = Str("/4]") });
        body.Add(new EX_Let { Value = L("ZSPT_New2"), Variable = LV("ZSPT_New2"),
                              Expression = Call(concat, LV("ZSPT_New2"), LV("ZSPT_S2")) });
        body.Add(new EX_Let { Value = L("ZSPT_T2"), Variable = LV("ZSPT_T2"), Expression = Call(s2t, LV("ZSPT_New2")) });
        body.Add(VCall(LV("ZSPT_M2"), "SetText", LV("ZSPT_T2")));

        // white below rank 1, orange while it is climbing, green once it is maxed
        Mark("COLOURLINE");
        body.AddRange(SetColour("ZSPT_M2", 1.0f, 1.0f, 1.0f));
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(geInt, LV("ZSPT_Lvl"), Int(1)) });
        body.Add(IfNot("TITLE"));
        body.AddRange(SetColour("ZSPT_M2", 1.0f, 0.55f, 0.1f));
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(geInt, LV("ZSPT_Lvl"), Int(4)) });
        body.Add(IfNot("TITLE"));
        body.AddRange(SetColour("ZSPT_M2", 0.24f, 0.95f, 0.36f));

        // ---------------- the title: a mark and green, only once mastered ----------------
        Mark("TITLE");
        // Same ladder as the mastery line. Rank 0 needs nothing: the reset happened at CHANGED.
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(geInt, LV("ZSPT_Lvl"), Int(1)) });
        body.Add(IfNot("RET"));
        body.AddRange(SetColour("ZSPT_Title", 1.0f, 0.55f, 0.1f));
        body.Add(new EX_LetBool { VariableExpression = LV("ZSPT_B"),
                                  AssignmentExpression = Call(geInt, LV("ZSPT_Lvl"), Int(4)) });
        body.Add(IfNot("RET"));
        body.Add(new EX_Let { Value = L("ZSPT_New"), Variable = LV("ZSPT_New"),
                              Expression = Call(concat, LV("ZSPT_S"), LV("ZSPT_M")) });
        body.Add(new EX_Let { Value = L("ZSPT_NewT"), Variable = LV("ZSPT_NewT"), Expression = Call(s2t, LV("ZSPT_New")) });
        body.Add(VCall(LV("ZSPT_Title"), "SetText", LV("ZSPT_NewT")));
        body.AddRange(SetColour("ZSPT_Title", 0.24f, 0.95f, 0.36f));
        body.Add(new EX_Let { Value = C("ZSPT_LastTitle"), Variable = CV("ZSPT_LastTitle"), Expression = LV("ZSPT_New") });
        body.Add(Goto("RET"));

        Mark("NEXT");
        body.Add(new EX_Let { Value = L("ZSPT_I"), Variable = LV("ZSPT_I"), Expression = Call(addInt, LV("ZSPT_I"), Int(1)) });
        body.Add(Goto("LOOP"));

        Mark("RET");
        body.Add(new EX_Return { ReturnExpression = new EX_Nothing() });
        body.Add(new EX_EndOfScript());

        // Both jump tokens are fixed width, so one measuring pass is enough.
        tick.ScriptBytecode = body.ToArray();
        var offs = new int[body.Count];
        for (int i = 0, cur = 0; i < body.Count; i++) { offs[i] = cur; cur += Measure(body[i]); }
        foreach (var (stmt, label) in jumps)
        {
            if (!labels.TryGetValue(label, out int target)) throw new Exception("unknown label " + label);
            switch (stmt)
            {
                case EX_Jump j: j.CodeOffset = (uint)offs[target]; break;
                case EX_JumpIfNot jn: jn.CodeOffset = (uint)offs[target]; break;
            }
        }
        tick.ScriptBytecode = body.ToArray();

        // ---------- register on the class ----------
        if (cls.FuncMap == null) throw new Exception("class has no FuncMap");
        cls.FuncMap.Add(FName.FromString(asset, "Tick"), new FPackageIndex(tickIdx));
        cls.Children = (cls.Children ?? Array.Empty<FPackageIndex>()).Append(new FPackageIndex(tickIdx)).ToArray();
        Console.WriteLine($"  + Tick event ({tick.ScriptBytecode.Length} statements), FuncMap + Children registered");


        ModAsset.ValidateJumps(tick, "WBP_GalaxyPieTooltip.Tick");

        // Hold the hand-built Tick against a real one, for fields the cooker fills in.
        var refPath = System.IO.Path.Combine(root, "tools", "raw", "ssw_raw", "StarSystemWidgetV2.uasset");
        if (System.IO.File.Exists(refPath))
        {
            var reference = ModAsset.Load(refPath);
            var refTick = reference.Asset.Exports.OfType<FunctionExport>()
                                   .First(f => f.ObjectName.ToString() == "Tick");
            KismetSerializer.asset = asset;
            var problems = ModAsset.CompareExportShape(tick, refTick, "Tick");
            if (problems.Count > 0)
                throw new Exception("shape differs from a real Blueprint Tick:" + Environment.NewLine
                                    + "  " + string.Join(Environment.NewLine + "  ", problems));
            Console.WriteLine("  shape check against StarSystemWidgetV2.Tick: OK");
        }
        else Console.WriteLine("  (no reference Tick available - shape check skipped)");
        var outPath = System.IO.Path.Combine(outRoot, AssetSubDir, "WBP_GalaxyPieTooltip.uasset");
        A.WriteAndVerify(outPath, "WBP_GalaxyPieTooltip");
        Console.WriteLine("OK");
    }

}
