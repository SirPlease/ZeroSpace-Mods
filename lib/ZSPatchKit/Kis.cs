// Kismet expression builders, usable once a ModAsset is loaded. Changing one changes the
// bytecode every mod ships.

using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace ZSPatchKit;

public static class Kis
{
    /// Bytes a statement takes once written. EX_Context.Offset counts bytes, not statements.
    public static int Measure(KismetExpression e)
    {
        int i = 0;
        KismetSerializer.SerializeExpression(e, ref i, false);
        return i;
    }

    public static KismetPropertyPointer Ptr(FName name, FPackageIndex owner) =>
        new KismetPropertyPointer { New = new FFieldPath { Path = new[] { name }, ResolvedOwner = owner } };

    public static KismetPropertyPointer NullPtr() =>
        new KismetPropertyPointer { New = new FFieldPath { Path = Array.Empty<FName>(), ResolvedOwner = new FPackageIndex(0) } };

    public static EX_CallMath Call(FPackageIndex fn, params KismetExpression[] args) =>
        new EX_CallMath { StackNode = fn, Parameters = args };

    public static EX_StringConst Str(string s) => new EX_StringConst { Value = s };
    public static EX_IntConst Int(int v) => new EX_IntConst { Value = v };
    public static EX_ByteConst Byte(byte v) => new EX_ByteConst { Value = v };
    public static EX_FloatConst Flt(float f) => new EX_FloatConst { Value = f };

    /// obj.&lt;prop&gt; read
    public static EX_Context ReadMember(KismetExpression objLocal, FName prop, FPackageIndex ownerCls)
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

    /// obj.&lt;NativeFn&gt;(args). RValuePointer is the local the result lands in.
    public static EX_Context CallOn(KismetExpression objLocal, FPackageIndex fn,
                                    KismetPropertyPointer? rv, params KismetExpression[] args)
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

    /// reach into a struct member, and again for a member of that member
    public static EX_StructMemberContext SM(KismetExpression inner, FName prop, FPackageIndex structTy) =>
        new EX_StructMemberContext { StructMemberExpression = Ptr(prop, structTy), StructExpression = inner };

    /// A static library call: the target is the library's class-default object.
    public static EX_Context LibCall(FPackageIndex defaultObj, FPackageIndex fn, params KismetExpression[] args)
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

    /// A bool property shaped the way the compiler emits them; CheckLocals() rejects any
    /// other layout.
    public static FBoolProperty BoolProp() => new FBoolProperty
    {
        ElementSize = 1,
        FieldSize = 1,
        ByteOffset = 0,
        ByteMask = 1,
        FieldMask = 255,
        NativeBool = true,
        Value = false,
    };
}
