// Team Colors
//
// Patches the in-match HUD widget (RTSSampleHUDWidget) so that every player in the
// match is recolored by their relation to you: yourself, your teammate, a mission
// AI ally, an enemy. Colors come from the mod's settings, or from built-in
// defaults when there are no settings to read.

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

class Program
{
    const string DefaultInput = @"mods\teamcolors\raw\hud\RTSSampleHUDWidget.uasset";
    const string DefaultOutput = @"mods\teamcolors\pak_build";
    const string AssetSubDir = @"Zerospace\Content\RTSGameSample\UI";

    // Where the mod manager keeps its saved settings. Missing file means the manager
    // is not installed, and the defaults below are used instead.
    const string SlotName = "ZSModSettings";
    const string SavePkgPath = "/Game/Mods/ZSModManager/ZSModSettingsSave";
    const string SaveClsName = "ZSModSettingsSave_C";

    const float LoopSeconds = 2.0f;
    // Both Delay calls share this id on purpose: Delay does nothing if a call with
    // the same id is already waiting, so the loop can never run twice over itself.
    const int LatentUuid = 424242001;

    // The game's color list, in the order the game numbers it. The position in this
    // list is the value stored on the player and the value the settings dropdown
    // saves, so nothing here may be reordered.
    static readonly string[] Palette = {
        "Blue", "Red", "Green", "Yellow", "Magenta", "Purple", "Cyan", "Orange",
        "Sea Green", "Light Pink", "Light Purple", "Pink", "Brown", "Dark Olive",
        "Teal", "Steel Blue", "Light Violet", "White", "Black",
    };

    const string DefaultSelf = "Green";
    const string DefaultTeammate = "Blue";
    const string DefaultMissionAI = "Light Purple";
    const string DefaultEnemy = "Red";
    const bool DefaultMinimapColors = true;

    static UAsset asset = null!;

    // how many bytes a statement takes once written out, for placing the new block
    static int Measure(KismetExpression e)
    {
        int i = 0;
        KismetSerializer.SerializeExpression(e, ref i, false);
        return i;
    }

    // ---------- imports ----------

    static int FindImport(string objectName, string className = "")
    {
        for (int i = 0; i < asset.Imports.Count; i++)
            if (asset.Imports[i].ObjectName.ToString() == objectName &&
                (className == "" || asset.Imports[i].ClassName.ToString() == className))
                return -(i + 1);
        return 0;
    }
    static FPackageIndex AddImport(string classPackage, string className, FPackageIndex outer, string objectName)
    {
        asset.Imports.Add(new Import(
            FName.FromString(asset, classPackage), FName.FromString(asset, className),
            outer, FName.FromString(asset, objectName), false));
        Console.WriteLine($"  +import {className} {objectName}");
        return new FPackageIndex(-asset.Imports.Count);
    }
    static FPackageIndex EnsurePackage(string pkg)
    {
        int i = FindImport(pkg, "Package");
        return i != 0 ? new FPackageIndex(i) : AddImport("/Script/CoreUObject", "Package", new FPackageIndex(0), pkg);
    }
    static FPackageIndex EnsureClass(string scriptPkg, string cls)
    {
        int i = FindImport(cls, "Class");
        return i != 0 ? new FPackageIndex(i) : AddImport("/Script/CoreUObject", "Class", EnsurePackage(scriptPkg), cls);
    }
    // Only import a function that really lives in the class it is named under. If the
    // game cannot find it, the call is left null and the game crashes on the spot.
    static FPackageIndex EnsureFn(string scriptPkg, string owningClass, string fn)
    {
        int i = FindImport(fn, "Function");
        return i != 0 ? new FPackageIndex(i) : AddImport("/Script/CoreUObject", "Function", EnsureClass(scriptPkg, owningClass), fn);
    }
    static FPackageIndex EnsureStruct(string scriptPkg, string name)
    {
        int i = FindImport(name, "ScriptStruct");
        return i != 0 ? new FPackageIndex(i) : AddImport("/Script/CoreUObject", "ScriptStruct", EnsurePackage(scriptPkg), name);
    }
    // class-default object import (e.g. Default__KismetArrayLibrary), the shape the
    // game itself uses to call a static library function
    static FPackageIndex EnsureDefaultObject(string scriptPkg, string cls)
    {
        string n = "Default__" + cls;
        int i = FindImport(n);
        return i != 0 ? new FPackageIndex(i) : AddImport(scriptPkg, cls, EnsurePackage(scriptPkg), n);
    }
    static FPackageIndex ImportSaveClass()
    {
        int i = FindImport(SaveClsName);
        return i != 0 ? new FPackageIndex(i)
            : AddImport("/Script/Engine", "BlueprintGeneratedClass", EnsurePackage(SavePkgPath), SaveClsName);
    }

    // ---------- expression builders ----------

    static KismetPropertyPointer Ptr(FName name, FPackageIndex owner) =>
        new KismetPropertyPointer { New = new FFieldPath { Path = new[] { name }, ResolvedOwner = owner } };
    static KismetPropertyPointer NullPtr() =>
        new KismetPropertyPointer { New = new FFieldPath { Path = Array.Empty<FName>(), ResolvedOwner = new FPackageIndex(0) } };

    static EX_CallMath Call(FPackageIndex fn, params KismetExpression[] args) =>
        new EX_CallMath { StackNode = fn, Parameters = args };
    static EX_StringConst Str(string s) => new EX_StringConst { Value = s };
    static EX_IntConst Int(int v) => new EX_IntConst { Value = v };
    static EX_ByteConst Byte(byte v) => new EX_ByteConst { Value = v };

    // obj.<prop> read
    static EX_Context ReadMember(KismetExpression objLocal, FName prop, FPackageIndex ownerCls)
    {
        var iv = new EX_InstanceVariable { Variable = Ptr(prop, ownerCls) };
        return new EX_Context
        {
            ObjectExpression = objLocal,
            Offset = (uint)Measure(iv),
            RValuePointer = Ptr(prop, ownerCls),
            ContextExpression = iv,
        };
    }

    // obj.<NativeFn>(args). RValuePointer is the local the result lands in when the
    // call is consumed by an assignment, which is how the game writes these calls.
    static EX_Context CallOn(KismetExpression objLocal, FPackageIndex fn, KismetPropertyPointer? rv, params KismetExpression[] args)
    {
        var ff = new EX_FinalFunction { StackNode = fn, Parameters = args };
        return new EX_Context
        {
            ObjectExpression = objLocal,
            Offset = (uint)Measure(ff),
            RValuePointer = rv ?? NullPtr(),
            ContextExpression = ff,
        };
    }

    // reach into a struct member, and again for a member of that member
    static EX_StructMemberContext SM(KismetExpression inner, FName prop, FPackageIndex structTy) =>
        new EX_StructMemberContext { StructMemberExpression = Ptr(prop, structTy), StructExpression = inner };

    static EX_Context ArrayLib(FPackageIndex defaultObj, FPackageIndex fn, params KismetExpression[] args)
    {
        var ff = new EX_FinalFunction { StackNode = fn, Parameters = args };
        return new EX_Context
        {
            ObjectExpression = new EX_ObjectConst { Value = defaultObj },
            Offset = (uint)Measure(ff),
            RValuePointer = NullPtr(),
            ContextExpression = ff,
        };
    }

    // Default paths are relative to the repo, and `dotnet run` starts in the project
    // folder, so climb up until the vanilla asset comes into view.
    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, DefaultInput))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }

    static void Main(string[] args)
    {
        var root = RepoRoot();
        var inPath = args.Length > 0 ? args[0] : Path.Combine(root, DefaultInput);
        var outDir = Path.Combine(args.Length > 1 ? args[1] : Path.Combine(root, DefaultOutput), AssetSubDir);

        byte ColorIdx(string name)
        {
            int i = Array.IndexOf(Palette, name);
            if (i < 0) throw new Exception($"unknown color '{name}'");
            return (byte)i;
        }
        byte defSelf = ColorIdx(DefaultSelf), defMate = ColorIdx(DefaultTeammate),
             defMission = ColorIdx(DefaultMissionAI), defEnemy = ColorIdx(DefaultEnemy);
        bool defMinimap = DefaultMinimapColors;
        Console.WriteLine($"defaults: self={defSelf} mate={defMate} mission={defMission} enemy={defEnemy} minimap={defMinimap}");

        Directory.CreateDirectory(outDir);
        asset = new UAsset(inPath, EngineVersion.VER_UE4_27);
        if (!asset.VerifyBinaryEquality()) throw new Exception("HUD donor: round-trip not binary-equal");
        KismetSerializer.asset = asset;

        var uber = asset.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString().StartsWith("ExecuteUbergraph"));
        var construct = asset.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == "Construct");
        int uberIdx1 = asset.Exports.IndexOf(uber) + 1;   // 1-based, for local property owners

        // Everything the patch leans on is checked here rather than assumed, so a game
        // update that moves this code fails the build instead of shipping a broken pak.
        var uberJson = KismetSerializer.SerializeScript(uber.ScriptBytecode);
        var code = uber.ScriptBytecode.ToList();
        int eosOff = (int)uberJson[uberJson.Count - 1]!["StatementIndex"]!;
        if (!(code[^1] is EX_EndOfScript)) throw new Exception("ubergraph does not end with EndOfScript");
        var returns = code.Select((e, i) => (e, i)).Where(t => t.e is EX_Return).ToList();
        if (returns.Count != 1) throw new Exception($"expected exactly 1 EX_Return, found {returns.Count}");
        int retOff = (int)uberJson[returns[0].i]!["StatementIndex"]!;
        if (!(code[0] is EX_PushExecutionFlow pf) || pf.PushingAddress != (uint)retOff)
            throw new Exception("ubergraph stmt 0 is not pushflow(Return) - flow convention changed");

        // Construct is a stub that calls the ubergraph at one entry point. Sending that
        // entry somewhere else is a same-size change to a single number, so nothing in
        // the existing code moves.
        var conCall = construct.ScriptBytecode.OfType<EX_LocalFinalFunction>().Single();
        var conEntry = (EX_IntConst)conCall.Parameters.Single();
        int vanillaEntry = conEntry.Value;
        Console.WriteLine($"ubergraph: {code.Count} stmts, EOS {eosOff}, Return {retOff}, Construct entry {vanillaEntry}");

        // This widget already calls Delay itself, so the call shape and its imports are
        // taken from the game's own copy rather than authored from scratch.
        EX_CallMath? vanillaDelay = null;
        foreach (var st in code)
            if (st is EX_CallMath cm && cm.StackNode.Index < 0
                && asset.Imports[-cm.StackNode.Index - 1].ObjectName.ToString() == "Delay"
                && cm.Parameters.Length == 3 && cm.Parameters[2] is EX_StructConst)
            { vanillaDelay = cm; break; }
        if (vanillaDelay == null) throw new Exception("no vanilla Delay call found in ubergraph");
        var vanillaLatent = (EX_StructConst)vanillaDelay.Parameters[2];
        var fnDelay = vanillaDelay.StackNode;
        Console.WriteLine($"vanilla Delay found (LatentActionInfo StructSize {vanillaLatent.StructSize})");

        // ---- imports ----
        var fnLoad = EnsureFn("/Script/Engine", "GameplayStatics", "LoadGameFromSlot");
        var fnIsValid = EnsureFn("/Script/Engine", "KismetSystemLibrary", "IsValid");
        var fnLess = EnsureFn("/Script/Engine", "KismetMathLibrary", "Less_IntInt");
        var fnAdd = EnsureFn("/Script/Engine", "KismetMathLibrary", "Add_IntInt");
        var fnGeInt = EnsureFn("/Script/Engine", "KismetMathLibrary", "GreaterEqual_IntInt");
        var fnLeInt = EnsureFn("/Script/Engine", "KismetMathLibrary", "LessEqual_IntInt");
        var fnB2Int = EnsureFn("/Script/Engine", "KismetMathLibrary", "Conv_ByteToInt");
        var fnEqB = EnsureFn("/Script/Engine", "KismetMathLibrary", "EqualEqual_ByteByte");
        var fnNeObj = EnsureFn("/Script/Engine", "KismetMathLibrary", "NotEqual_ObjectObject");
        var fnNot = EnsureFn("/Script/Engine", "KismetMathLibrary", "Not_PreBool");
        var fnI2Byte = EnsureFn("/Script/Engine", "KismetMathLibrary", "Conv_IntToByte");
        var fnArrGet = EnsureFn("/Script/Engine", "KismetArrayLibrary", "Array_Get");
        var fnArrLen = EnsureFn("/Script/Engine", "KismetArrayLibrary", "Array_Length");
        var arrLibObj = EnsureDefaultObject("/Script/Engine", "KismetArrayLibrary");
        var fnGetTeamInfo = EnsureFn("/Script/RTSPlugin", "RTSPlayerController", "GetTeamInfo");
        var fnChangeColor = EnsureFn("/Script/RTS", "NovaPlayerState", "ChangePlayerColor");
        var fnGetDispColor = EnsureFn("/Script/RTS", "NovaPlayerState", "GetDisplayedColor");
        // ChangePlayerColor is the game's authoritative setter and only does anything on
        // the machine hosting the match. ChangeUnitTeamColor is the display-only one, so
        // it is what makes the colors appear when you have joined someone else's game.
        var fnChangeUnitColor = EnsureFn("/Script/RTS", "NovaPlayerState", "ChangeUnitTeamColor");
        var fnGetOwnedUnits = EnsureFn("/Script/RTS", "NovaPlayerState", "GetOwnedUnits");
        var clsActor = EnsureClass("/Script/Engine", "Actor");
        var clsNovaPC = EnsureClass("/Script/RTS", "NovaPlayerController");
        var clsNovaPS = EnsureClass("/Script/RTS", "NovaPlayerState");
        var clsTeamInfo = EnsureClass("/Script/RTSPlugin", "RTSTeamInfo");
        var clsPlayerState = EnsureClass("/Script/Engine", "PlayerState");
        var clsSaveGame = EnsureClass("/Script/Engine", "SaveGame");
        var stLobbyInfo = EnsureStruct("/Script/RTS", "NovaLobbyInfo");
        var stTeam = EnsureStruct("/Script/RTS", "NovaLobbyTeamInfo");
        var stSlot = EnsureStruct("/Script/RTS", "NovaPlayerStartSlot");
        var stBound = EnsureStruct("/Script/RTS", "NovaLobbyBoundPlayerInfo");
        var saveCls = ImportSaveClass();
        // this one the widget already imports for its own use, so ask for it rather
        // than adding a second copy
        int giSettings = FindImport("GetNovaGameUserSettings", "Function");
        if (giSettings == 0) throw new Exception("GetNovaGameUserSettings import missing from HUD");
        var fnGetSettings = new FPackageIndex(giSettings);
        var clsNovaGUS = EnsureClass("/Script/RTS", "NovaGameUserSettings");
        var fnSetVisual = EnsureFn("/Script/RTS", "NovaGameUserSettings", "SetVisualColoring");
        var fnNeByte = EnsureFn("/Script/Engine", "KismetMathLibrary", "NotEqual_ByteByte");

        // ---- locals ----
        // New locals go on the end of the list, so every existing one keeps its place.
        // An authored property has to fill in all of the common fields, not only the
        // ones its type needs: leave ArrayDim or RepNotifyFunc out and the game either
        // crashes while loading the asset or reads a garbage name.
        FProperty CommonLocal(FProperty p, string name, string ser, int elem)
        {
            p.Name = FName.FromString(asset, name);
            p.SerializedType = FName.FromString(asset, ser);
            p.Flags = EObjectFlags.RF_Public;
            p.ArrayDim = EArrayDim.TArray;
            p.ElementSize = elem;
            p.PropertyFlags = EPropertyFlags.CPF_None;
            p.RepNotifyFunc = FName.FromString(asset, "None");
            p.BlueprintReplicationCondition = ELifetimeCondition.COND_None;
            return p;
        }
        Func<KismetExpression> Local(string name, string kind, FPackageIndex typeRef = default, int structSize = 0)
        {
            var fname = FName.FromString(asset, name);
            if (!uber.LoadedProperties.Any(p => p.Name.ToString() == name))
            {
                FProperty p = kind switch
                {
                    "object" => CommonLocal(new FObjectProperty { PropertyClass = typeRef }, name, "ObjectProperty", 8),
                    "bool" => CommonLocal(new FBoolProperty
                    {
                        FieldSize = 1, ByteOffset = 0, ByteMask = 1, FieldMask = 255, NativeBool = true, Value = false,
                    }, name, "BoolProperty", 1),
                    "int" => CommonLocal(new FGenericProperty(), name, "IntProperty", 4),
                    "byte" => CommonLocal(new FByteProperty { Enum = new FPackageIndex(0) }, name, "ByteProperty", 1),
                    "struct" => CommonLocal(new FStructProperty { Struct = typeRef }, name, "StructProperty", structSize),
                    // an array property carries a second property for its element type,
                    // which shares the array's name
                    "array" => CommonLocal(new FArrayProperty
                    {
                        Inner = CommonLocal(new FObjectProperty { PropertyClass = typeRef }, name, "ObjectProperty", 8),
                    }, name, "ArrayProperty", 16),
                    _ => throw new Exception(kind),
                };
                uber.LoadedProperties = uber.LoadedProperties.Append(p).ToArray();
            }
            return () => new EX_LocalVariable { Variable = Ptr(fname, new FPackageIndex(uberIdx1)) };
        }

        var lSave = Local("ZSTC_Save", "object", clsSaveGame);
        var lB = Local("ZSTC_B", "bool");
        var lPCraw = Local("ZSTC_PCRaw", "object", EnsureClass("/Script/Engine", "PlayerController"));
        var lPC = Local("ZSTC_PC", "object", clsNovaPC);
        var lTeamObj = Local("ZSTC_TeamObj", "object", clsTeamInfo);
        var lMyTeam = Local("ZSTC_MyTeam", "byte");
        var lLobby = Local("ZSTC_Lobby", "struct", stLobbyInfo, 32);
        var lTeam = Local("ZSTC_Team", "struct", stTeam, 48);
        var lSlot = Local("ZSTC_Slot", "struct", stSlot, 112);
        var lI = Local("ZSTC_I", "int");
        var lJ = Local("ZSTC_J", "int");
        var lNT = Local("ZSTC_NT", "int");
        var lNS = Local("ZSTC_NS", "int");
        var lPS = Local("ZSTC_PS", "object", clsPlayerState);
        var lNPS = Local("ZSTC_NPS", "object", clsNovaPS);
        var lSelf = Local("ZSTC_SelfB", "byte");
        var lMate = Local("ZSTC_MateB", "byte");
        var lMission = Local("ZSTC_MissionB", "byte");
        var lEnemy = Local("ZSTC_EnemyB", "byte");
        var lTarget = Local("ZSTC_Target", "byte");
        var lCur = Local("ZSTC_Cur", "byte");
        var lMini = Local("ZSTC_Mini", "bool");
        var lGUS = Local("ZSTC_GUS", "object", clsNovaGUS);
        var lUnits = Local("ZSTC_Units", "array", clsActor);
        var lUnit = Local("ZSTC_Unit", "object", clsActor);
        var lK = Local("ZSTC_K", "int");
        var lNU = Local("ZSTC_NU", "int");

        KismetPropertyPointer LP(string name) => Ptr(FName.FromString(asset, name), new FPackageIndex(uberIdx1));

        EX_Let LetInt(string dst, KismetExpression expr) => new EX_Let { Value = LP(dst), Variable = new EX_LocalVariable { Variable = LP(dst) }, Expression = expr };
        EX_Let LetByte(string dst, KismetExpression expr) => new EX_Let { Value = LP(dst), Variable = new EX_LocalVariable { Variable = LP(dst) }, Expression = expr };
        EX_Let LetStruct(string dst, KismetExpression expr) => new EX_Let { Value = LP(dst), Variable = new EX_LocalVariable { Variable = LP(dst) }, Expression = expr };
        EX_Let LetArr(string dst, KismetExpression expr) => new EX_Let { Value = LP(dst), Variable = new EX_LocalVariable { Variable = LP(dst) }, Expression = expr };
        EX_LetObj LetObj(Func<KismetExpression> dst, KismetExpression expr) => new EX_LetObj { VariableExpression = dst(), AssignmentExpression = expr };
        EX_LetBool LetBool(Func<KismetExpression> dst, KismetExpression expr) => new EX_LetBool { VariableExpression = dst(), AssignmentExpression = expr };

        FName P(string s) => FName.FromString(asset, s);
        EX_Context Setting(string prop) => ReadMember(lSave(), P(prop), saveCls);

        // The Linkage field is where the game re-enters this graph when the wait is
        // over. It can only be filled in once the block below has been laid out.
        (EX_CallMath call, EX_SkipOffsetConst linkage) MakeDelay()
        {
            var link = new EX_SkipOffsetConst { Value = 0 };
            var call = Call(fnDelay,
                new EX_Self(),
                new EX_FloatConst { Value = LoopSeconds },
                new EX_StructConst
                {
                    Struct = vanillaLatent.Struct,
                    StructSize = vanillaLatent.StructSize,
                    Value = new KismetExpression[]
                    {
                        link,
                        Int(LatentUuid),
                        new EX_NameConst { Value = FName.FromString(asset, uber.ObjectName.ToString()) },
                        new EX_Self(),
                    },
                });
            return (call, link);
        }
        var (delayFirst, linkFirst) = MakeDelay();
        var (delayRe, linkRe) = MakeDelay();

        // ---- the new code ----
        var body = new List<(string? Label, KismetExpression Ex, object? Jump)>();   // Jump: label name or absolute offset
        void Emit(KismetExpression e, string? label = null, object? jump = null) => body.Add((label, e, jump));

        // Entered from Construct: start the timer, then run what Construct did before.
        Emit(delayFirst, label: "TAIL_FIRST");
        Emit(new EX_Jump(), jump: vanillaEntry);

        // Entered every couple of seconds from the timer. Read the settings, and fall
        // back to the defaults if there are none. Turning the mod off leaves the loop
        // running but applies nothing, so turning it back on mid match works.
        Emit(LetByte("ZSTC_SelfB", Byte(defSelf)), label: "TAIL_RE");
        Emit(LetByte("ZSTC_MateB", Byte(defMate)));
        Emit(LetByte("ZSTC_MissionB", Byte(defMission)));
        Emit(LetByte("ZSTC_EnemyB", Byte(defEnemy)));
        Emit(LetBool(lMini, defMinimap ? new EX_True() : (KismetExpression)new EX_False()));
        Emit(LetObj(lSave, Call(fnLoad, Str(SlotName), Int(0))));
        Emit(LetBool(lB, Call(fnIsValid, lSave())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "APPLY");
        Emit(new EX_JumpIfNot { BooleanExpression = Setting("ZSTeamColors_Enabled") }, jump: "SCHED");
        Emit(LetByte("ZSTC_SelfB", Call(fnI2Byte, Setting("ZSTeamColors_SelfColorIdx"))));
        Emit(LetByte("ZSTC_MateB", Call(fnI2Byte, Setting("ZSTeamColors_TeammateColorIdx"))));
        Emit(LetByte("ZSTC_MissionB", Call(fnI2Byte, Setting("ZSTeamColors_MissionAIColorIdx"))));
        Emit(LetByte("ZSTC_EnemyB", Call(fnI2Byte, Setting("ZSTeamColors_EnemyColorIdx"))));
        Emit(LetBool(lMini, Setting("ZSTeamColors_MinimapColors")));

        // The minimap toggle is an either/or the game itself imposes. Its native drawing
        // code has two paths, and only one of them exists at a time:
        //   on  -> every unit is drawn in its owner's color, so a mission AI ally is
        //          told apart from a teammate. The pale green glow on selected units is
        //          not drawn in this path.
        //   off -> the game's usual minimap, glow included, but every ally shares one
        //          color, so mission AI looks like a teammate.
        // Having both would mean changing the game's own code, which this mod does not
        // do. The ON branch also sets the unit color mode, because the per player
        // minimap path is only used while that mode is picked.
        EX_Context ApplySettings() => new EX_Context
        {
            ObjectExpression = lGUS(),
            Offset = (uint)Measure(new EX_VirtualFunction
            { VirtualFunctionName = FName.FromString(asset, "ApplyNonResolutionSettings"), Parameters = Array.Empty<KismetExpression>() }),
            RValuePointer = NullPtr(),
            ContextExpression = new EX_VirtualFunction
            { VirtualFunctionName = FName.FromString(asset, "ApplyNonResolutionSettings"), Parameters = Array.Empty<KismetExpression>() },
        };
        EX_LetBool SetFlag(bool on) => new EX_LetBool
        {
            VariableExpression = ReadMember(lGUS(), P("bMinimapColorUsesPlayerColor"), clsNovaGUS),
            AssignmentExpression = on ? new EX_True() : (KismetExpression)new EX_False(),
        };
        Emit(LetObj(lGUS, Call(fnGetSettings)), label: "APPLY");
        Emit(LetBool(lB, Call(fnIsValid, lGUS())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "COLORS");
        Emit(new EX_JumpIfNot { BooleanExpression = lMini() }, jump: "MINI_OFF");
        Emit(LetBool(lB, Call(fnNot, ReadMember(lGUS(), P("bMinimapColorUsesPlayerColor"), clsNovaGUS))));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "CHK_MODE");
        Emit(new EX_Jump(), jump: "DO_SET");
        Emit(LetBool(lB, Call(fnNeByte, ReadMember(lGUS(), P("VisualColoring"), clsNovaGUS), Byte(0))), label: "CHK_MODE");
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "COLORS");          // both already right
        Emit(SetFlag(true), label: "DO_SET");
        Emit(CallOn(lGUS(), fnSetVisual, null, Byte(0), new EX_False()));
        Emit(ApplySettings());
        Emit(new EX_Jump(), jump: "COLORS");
        // Toggle off only clears the minimap flag. The unit color mode is left as the
        // player set it, since it also affects what units look like on the field.
        Emit(new EX_JumpIfNot
        { BooleanExpression = ReadMember(lGUS(), P("bMinimapColorUsesPlayerColor"), clsNovaGUS) }, label: "MINI_OFF", jump: "COLORS");
        Emit(SetFlag(false));
        Emit(ApplySettings());

        // Walk the match's teams and slots. Everyone in the match is in here, mission
        // AI and enemies included.
        Emit(LetObj(lPCraw, new EX_VirtualFunction
        {
            VirtualFunctionName = FName.FromString(asset, "GetOwningPlayer"),
            Parameters = Array.Empty<KismetExpression>(),
        }), label: "COLORS");
        Emit(LetObj(lPC, new EX_DynamicCast { ClassPtr = clsNovaPC, Target = lPCraw() }));
        Emit(LetBool(lB, Call(fnIsValid, lPC())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "SCHED");
        Emit(LetObj(lTeamObj, CallOn(lPC(), fnGetTeamInfo, LP("ZSTC_TeamObj"))));
        Emit(LetBool(lB, Call(fnIsValid, lTeamObj())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "SCHED");
        Emit(LetByte("ZSTC_MyTeam", ReadMember(lTeamObj(), P("TeamIndex"), clsTeamInfo)));
        Emit(LetStruct("ZSTC_Lobby", ReadMember(lPC(), P("CurrentMapLobbyInfo"), clsNovaPC)));
        Emit(LetInt("ZSTC_NT", ArrayLib(arrLibObj, fnArrLen, SM(lLobby(), P("Teams"), stLobbyInfo))));
        Emit(LetInt("ZSTC_I", Int(0)));

        Emit(LetBool(lB, Call(fnLess, lI(), lNT())), label: "TEAMLOOP");
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "SCHED");
        Emit(ArrayLib(arrLibObj, fnArrGet, SM(lLobby(), P("Teams"), stLobbyInfo), lI(), lTeam()));
        Emit(LetInt("ZSTC_NS", ArrayLib(arrLibObj, fnArrLen, SM(lTeam(), P("Slots"), stTeam))));
        Emit(LetInt("ZSTC_J", Int(0)));

        Emit(LetBool(lB, Call(fnLess, lJ(), lNS())), label: "SLOTLOOP");
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "NEXTTEAM");
        Emit(ArrayLib(arrLibObj, fnArrGet, SM(lTeam(), P("Slots"), stTeam), lJ(), lSlot()));
        Emit(LetObj(lPS, SM(SM(lSlot(), P("BoundPlayer"), stSlot), P("PlayerState"), stBound)));
        Emit(LetBool(lB, Call(fnIsValid, lPS())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "NEXTSLOT");
        Emit(LetObj(lNPS, new EX_DynamicCast { ClassPtr = clsNovaPS, Target = lPS() }));
        Emit(LetBool(lB, Call(fnIsValid, lNPS())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "NEXTSLOT");

        // Who is this. Another team means enemy. On your own team, the slot number says
        // whether the player is a real one or a mission AI the map script runs: slots 2
        // to 13 are real player slots, 14 to 25 are the map's own AI. A computer that
        // filled a real slot counts as a teammate, which is why nothing here asks
        // whether the player is a bot.
        Emit(LetBool(lB, Call(fnEqB, SM(lTeam(), P("TeamIndex"), stTeam), lMyTeam())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "SET_ENEMY");
        Emit(LetBool(lB, Call(fnGeInt,
            Call(fnB2Int, SM(lSlot(), P("PlayerSlotID"), stSlot)), Int(14))));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "CHK_SELF");
        Emit(LetBool(lB, Call(fnLeInt,
            Call(fnB2Int, SM(lSlot(), P("PlayerSlotID"), stSlot)), Int(25))));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "CHK_SELF");
        Emit(LetByte("ZSTC_Target", lMission()));
        Emit(new EX_Jump(), jump: "DO");
        Emit(LetBool(lB, Call(fnNeObj,
            SM(SM(lSlot(), P("BoundPlayer"), stSlot), P("Controller"), stBound), lPCraw())), label: "CHK_SELF");
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "SET_SELF");
        Emit(LetByte("ZSTC_Target", lMate()));
        Emit(new EX_Jump(), jump: "DO");
        Emit(LetByte("ZSTC_Target", lSelf()), label: "SET_SELF");
        Emit(new EX_Jump(), jump: "DO");
        Emit(LetByte("ZSTC_Target", lEnemy()), label: "SET_ENEMY");

        // Nothing to do if the color is already right, which is the normal case after
        // the first pass.
        Emit(LetByte("ZSTC_Cur", CallOn(lNPS(), fnGetDispColor, LP("ZSTC_Cur"))), label: "DO");
        Emit(LetBool(lB, Call(fnEqB, lCur(), lTarget())));
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "CHANGE");
        Emit(new EX_Jump(), jump: "NEXTSLOT");
        Emit(CallOn(lNPS(), fnChangeColor, null, lTarget()), label: "CHANGE");

        // Then recolor the player's units one by one, which is the part that works
        // when you are not hosting. Hosting makes the call above stick, so the color
        // then matches on the next pass and this loop is skipped from there on.
        // Both arguments are plain variables. A call nested into an argument here would
        // make the game read the wrong memory and crash.
        Emit(LetArr("ZSTC_Units", CallOn(lNPS(), fnGetOwnedUnits, LP("ZSTC_Units"))));
        Emit(LetInt("ZSTC_NU", ArrayLib(arrLibObj, fnArrLen, lUnits())));
        Emit(LetInt("ZSTC_K", Int(0)));
        Emit(LetBool(lB, Call(fnLess, lK(), lNU())), label: "UNITLOOP");
        Emit(new EX_JumpIfNot { BooleanExpression = lB() }, jump: "NEXTSLOT");
        Emit(ArrayLib(arrLibObj, fnArrGet, lUnits(), lK(), lUnit()));
        Emit(Call(fnChangeUnitColor, lUnit(), lTarget()));
        Emit(LetInt("ZSTC_K", Call(fnAdd, lK(), Int(1))));
        Emit(new EX_Jump(), jump: "UNITLOOP");

        Emit(LetInt("ZSTC_J", Call(fnAdd, lJ(), Int(1))), label: "NEXTSLOT");
        Emit(new EX_Jump(), jump: "SLOTLOOP");
        Emit(LetInt("ZSTC_I", Call(fnAdd, lI(), Int(1))), label: "NEXTTEAM");
        Emit(new EX_Jump(), jump: "TEAMLOOP");

        // Wait, then come back here. Ending the flow is how this graph finishes.
        Emit(delayRe, label: "SCHED");
        Emit(new EX_PopExecutionFlow());

        // ---- work out where everything landed, then fill in the jumps ----
        var offsets = new Dictionary<string, int>();
        int cur = eosOff;
        foreach (var (label, ex, _) in body)
        {
            if (label != null) offsets[label] = cur;
            cur += Measure(ex);
        }
        foreach (var (_, ex, jump) in body)
        {
            if (jump == null) continue;
            uint t = jump is string s2 ? (uint)offsets[s2] : (uint)(int)jump;
            switch (ex)
            {
                case EX_Jump j: j.CodeOffset = t; break;
                case EX_JumpIfNot jn: jn.CodeOffset = t; break;
                default: throw new Exception("jump on non-jump stmt");
            }
        }
        linkFirst.Value = (uint)offsets["TAIL_RE"];
        linkRe.Value = (uint)offsets["TAIL_RE"];
        Console.WriteLine($"tail: {body.Count} stmts @{eosOff}..{cur}; TAIL_FIRST {offsets["TAIL_FIRST"]}, TAIL_RE {offsets["TAIL_RE"]}");

        code.InsertRange(code.Count - 1, body.Select(b => b.Ex));
        uber.ScriptBytecode = code.ToArray();
        conEntry.Value = offsets["TAIL_FIRST"];
        Console.WriteLine($"Construct entry: {vanillaEntry} -> {conEntry.Value}");

        // ---- write it out, then read it back with a fresh parser and check it ----
        var outPath = Path.Combine(outDir, "RTSSampleHUDWidget.uasset");
        asset.Write(outPath);
        Console.WriteLine("written: " + outPath);

        var check = new UAsset(outPath, EngineVersion.VER_UE4_27);
        KismetSerializer.asset = check;
        var cu = check.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString().StartsWith("ExecuteUbergraph"));
        var cj = KismetSerializer.SerializeScript(cu.ScriptBytecode);
        var stmtOffsets = new HashSet<int>();
        foreach (var st in cj) stmtOffsets.Add((int)st!["StatementIndex"]!);
        Console.WriteLine($"reloaded: {cu.ScriptBytecode.Length} stmts");

        // Every jump has to point at the start of a statement. A jump into the middle of
        // one makes the game read whatever bytes are there as an instruction.
        var bad = new List<string>();
        void ScanTargets(Newtonsoft.Json.Linq.JToken tok)
        {
            if (tok is Newtonsoft.Json.Linq.JObject o)
            {
                var t = (string?)o["Token"];
                if (t == "EX_Jump" || t == "EX_JumpIfNot")
                {
                    int? tgt = (int?)(o["CodeOffset"] ?? o["Value"]);
                    if (tgt != null && !stmtOffsets.Contains(tgt.Value)) bad.Add($"{t} -> {tgt}");
                }
                if (t == "EX_PushExecutionFlow")
                {
                    int? tgt = (int?)o["PushingAddress"];
                    if (tgt != null && !stmtOffsets.Contains(tgt.Value)) bad.Add($"{t} -> {tgt}");
                }
                foreach (var pr in o.Properties()) ScanTargets(pr.Value);
            }
            else if (tok is Newtonsoft.Json.Linq.JArray a)
                foreach (var x in a) ScanTargets(x);
        }
        ScanTargets(cj);
        if (bad.Count > 0) throw new Exception("dangling jump targets:\n  " + string.Join("\n  ", bad.Distinct()));
        Console.WriteLine("jump-target scan: OK (no dangling targets)");

        var cc = check.Exports.OfType<FunctionExport>().First(e => e.ObjectName.ToString() == "Construct");
        var centry = (EX_IntConst)cc.ScriptBytecode.OfType<EX_LocalFinalFunction>().Single().Parameters.Single();
        if (centry.Value != offsets["TAIL_FIRST"]) throw new Exception("Construct entry not persisted");
        Console.WriteLine($"Construct entry persisted: {centry.Value}");

        int delays = 0;
        foreach (var st in cu.ScriptBytecode)
            if (st is EX_CallMath cm2 && cm2.StackNode.Index < 0
                && check.Imports[-cm2.StackNode.Index - 1].ObjectName.ToString() == "Delay"
                && cm2.Parameters is [_, _, EX_StructConst sc]
                && sc.Value is [EX_SkipOffsetConst so, ..]
                && so.Value == (uint)offsets["TAIL_RE"])
                delays++;
        if (delays != 2) throw new Exception($"expected 2 patched Delay calls, found {delays}");
        Console.WriteLine("latent linkage persisted on both Delay calls");
        Console.WriteLine("OK");
    }
}
