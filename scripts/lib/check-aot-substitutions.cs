// Guard for src/PenBridge.Mac/Trimming.Substitutions.xml (issue #42); run it through scripts/check-bundle.sh.
//
// The substitution stubs the public ObjCRuntime.Dlfcn.dlopen(string, int) (returns 0) so NSObject's static
// constructor stops preloading ~45 frameworks. That is only safe while the stubbed overload's sole callers are
// those preload fields and nobody reads them. For each linked Microsoft.macOS.dll given (ILLink output, the
// input to ILC), this verifies:
//  1. The stub is applied: dlopen(string, int32)'s body is `ldc.i4.0; conv.i; ret`.
//  2. Every call to it is inside Foundation.NSObject::.cctor and is immediately followed by a stsfld into a
//     Foundation.NSObject field.
//  3. None of those fields is read anywhere in the assembly (no ldsfld/ldsflda). They are private, so no
//     other assembly can read them.
// Exit code 0 = all pass, 1 = a check failed, 2 = usage error.
#pragma warning disable CA1303 // CLI output strings are not localized.

using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: check-aot-substitutions.cs <linked Microsoft.macOS.dll>...");
    return 2;
}

// IL opcode table from System.Reflection.Emit (one- and two-byte opcodes).
var oneByte = new OpCode?[256];
var twoByte = new OpCode?[256];
foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
{
    var op = (OpCode)f.GetValue(null)!;
    ushort v = (ushort)op.Value;
    if (op.Size == 1)
    {
        oneByte[v] = op;
    }
    else
    {
        twoByte[v & 0xFF] = op;
    }
}

// Method signature blob of `static native int dlopen(string, int32)`: default call conv, 2 params, I, STRING, I4.
byte[] dlopenSig = [0x00, 0x02, 0x18, 0x0E, 0x08];
const string Advice = "workload update changed the dlopen callers; re-validate src/PenBridge.Mac/Trimming.Substitutions.xml (skill mac-build-run, \"Memory and startup\")";

bool allOk = true;
foreach (string path in args)
{
    allOk &= Check(path);
}

return allOk ? 0 : 1;

bool Check(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"FAIL {path}: not found (publish the NativeAOT app first)");
        return false;
    }

    using var pe = new PEReader(File.OpenRead(path));
    MetadataReader md = pe.GetMetadataReader();
    var errors = new List<string>();

    MethodDefinitionHandle dlopen = default;
    foreach (MethodDefinitionHandle h in md.MethodDefinitions)
    {
        MethodDefinition m = md.GetMethodDefinition(h);
        if (md.GetString(m.Name) == "dlopen" && TypeName(md, m.GetDeclaringType()) == "ObjCRuntime.Dlfcn"
            && md.GetBlobBytes(m.Signature).AsSpan().SequenceEqual(dlopenSig))
        {
            dlopen = h;
        }
    }

    if (dlopen.IsNil)
    {
        Console.WriteLine($"FAIL {path}: ObjCRuntime.Dlfcn.dlopen(string, int32) not found; {Advice}");
        return false;
    }

    MethodDefinition dlopenDef = md.GetMethodDefinition(dlopen);
    byte[] stubBody = dlopenDef.RelativeVirtualAddress == 0 ? [] : pe.GetMethodBody(dlopenDef.RelativeVirtualAddress).GetILBytes()!;
    if (!stubBody.AsSpan().SequenceEqual(new byte[] { 0x16, 0xD3, 0x2A }))
    {
        errors.Add("dlopen(string, int32) is not stubbed (expected `ldc.i4.0; conv.i; ret`): the substitution was not applied");
    }

    var preloadFields = new HashSet<FieldDefinitionHandle>();
    var reads = new List<string>();
    int calls = 0;

    // Pass 1: callers and the stsfld after each call. Pass 2 (below): reads of the stored fields.
    var bodies = new List<(string Owner, byte[] Il)>();
    foreach (MethodDefinitionHandle h in md.MethodDefinitions)
    {
        MethodDefinition m = md.GetMethodDefinition(h);
        if (m.RelativeVirtualAddress == 0)
        {
            continue;
        }

        byte[] il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes() ?? [];
        string owner = TypeName(md, m.GetDeclaringType()) + "::" + md.GetString(m.Name);
        bodies.Add((owner, il));

        List<(int Offset, OpCode Op, int Token)> ins = Decode(il);
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].Op.OperandType != OperandType.InlineMethod || MetadataTokens.EntityHandle(ins[i].Token) != (EntityHandle)dlopen)
            {
                continue;
            }

            calls++;
            if (owner != "Foundation.NSObject::.cctor")
            {
                errors.Add($"dlopen(string, int32) is called from {owner} (IL_{ins[i].Offset:x4}); only NSObject's .cctor may call it");
                continue;
            }

            if (i + 1 >= ins.Count || ins[i + 1].Op.Value != OpCodes.Stsfld.Value
                || MetadataTokens.EntityHandle(ins[i + 1].Token).Kind != HandleKind.FieldDefinition)
            {
                errors.Add($"dlopen(string, int32) call at NSObject..cctor IL_{ins[i].Offset:x4} is not stored straight into a field");
                continue;
            }

            var field = (FieldDefinitionHandle)MetadataTokens.EntityHandle(ins[i + 1].Token);
            FieldDefinition fd = md.GetFieldDefinition(field);
            if (TypeName(md, fd.GetDeclaringType()) != "Foundation.NSObject")
            {
                errors.Add($"dlopen(string, int32) result at NSObject..cctor IL_{ins[i].Offset:x4} is stored outside NSObject");
                continue;
            }

            preloadFields.Add(field);
        }
    }

    foreach ((string owner, byte[] il) in bodies)
    {
        foreach ((int offset, OpCode op, int token) in Decode(il))
        {
            if ((op.Value == OpCodes.Ldsfld.Value || op.Value == OpCodes.Ldsflda.Value)
                && MetadataTokens.EntityHandle(token).Kind == HandleKind.FieldDefinition
                && preloadFields.Contains((FieldDefinitionHandle)MetadataTokens.EntityHandle(token)))
            {
                string name = md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(token)).Name);
                errors.Add($"NSObject.{name} (a dlopen result) is read in {owner} IL_{offset:x4}; the stub would hand it 0");
            }
        }
    }

    if (errors.Count == 0)
    {
        Console.WriteLine($"OK   {path}: dlopen(string, int32) stubbed; {calls} calls, all NSObject..cctor -> {preloadFields.Count} unread NSObject fields");
        return true;
    }

    Console.WriteLine($"FAIL {path}: {errors.Count} problem(s); {Advice}");
    foreach (string e in errors)
    {
        Console.WriteLine($"       {e}");
    }

    return false;
}

List<(int Offset, OpCode Op, int Token)> Decode(byte[] il)
{
    var result = new List<(int, OpCode, int)>();
    int pos = 0;
    while (pos < il.Length)
    {
        int start = pos;
        OpCode? op = il[pos] == 0xFE && pos + 1 < il.Length ? twoByte[il[++pos]] : oneByte[il[pos]];
        pos++;
        if (op is null)
        {
            throw new InvalidDataException($"unknown IL opcode at {start}");
        }

        int token = 0;
        switch (op.Value.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                pos += 1;
                break;
            case OperandType.InlineVar:
                pos += 2;
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                pos += 8;
                break;
            case OperandType.InlineSwitch:
                int n = BitConverter.ToInt32(il, pos);
                pos += 4 + (4 * n);
                break;
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineTok:
            case OperandType.InlineType:
                token = BitConverter.ToInt32(il, pos);
                pos += 4;
                break;
            default: // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR
                pos += 4;
                break;
        }

        result.Add((start, op.Value, token));
    }

    return result;
}

static string TypeName(MetadataReader md, TypeDefinitionHandle h)
{
    TypeDefinition t = md.GetTypeDefinition(h);
    string name = md.GetString(t.Name);
    TypeDefinitionHandle outer = t.GetDeclaringType();
    if (!outer.IsNil)
    {
        return TypeName(md, outer) + "/" + name;
    }

    string ns = md.GetString(t.Namespace);
    return ns.Length == 0 ? name : ns + "." + name;
}
