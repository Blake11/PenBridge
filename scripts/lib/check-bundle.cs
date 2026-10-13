// Bundle checker for scripts/check-bundle.sh (a .NET file-based app; run it through that script).
//
// For every directory given, loads each managed assembly and verifies:
//  1. Reference closure: every type a bundled assembly references in another assembly exists there
//     (as a type definition or a type forwarder). Catches the trimmer or the bundle copy dropping
//     an assembly, or types inside one, that kept code still needs (issue #46).
//  2. Pairing: if PenBridge.Core's PairingSession is in the bundle, BouncyCastle's J-PAKE types are too.
//     With --require-pairing, PairingSession itself must be present.
// Exit code 0 = all directories pass, 1 = a check failed, 2 = usage error.
#pragma warning disable CA1303 // CLI output strings are not localized.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

const string PairingType = "PenBridge.Core.Security.PairingSession";
const string BouncyCastle = "BouncyCastle.Cryptography";
string[] jpakeTypes =
[
    "Org.BouncyCastle.Crypto.Agreement.JPake.JPakeParticipant",
    "Org.BouncyCastle.Crypto.Agreement.JPake.JPakePrimeOrderGroups",
    "Org.BouncyCastle.Crypto.Digests.Sha256Digest",
    "Org.BouncyCastle.Math.BigInteger",
];

bool requirePairing = false;
var dirs = new List<string>();
foreach (string arg in args)
{
    if (arg == "--require-pairing")
    {
        requirePairing = true;
    }
    else if (arg.StartsWith('-'))
    {
        Console.Error.WriteLine($"error: unknown option {arg}");
        return 2;
    }
    else
    {
        dirs.Add(arg);
    }
}

if (dirs.Count == 0)
{
    Console.Error.WriteLine("usage: check-bundle.cs [--require-pairing] <assembly-dir>...");
    return 2;
}

bool allOk = true;
foreach (string dir in dirs)
{
    string root = dir;
    if (dir.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal) && Directory.Exists(Path.Combine(dir, "Contents", "MonoBundle")))
    {
        root = Path.Combine(dir, "Contents", "MonoBundle");
    }

    if (!Directory.Exists(root))
    {
        Console.WriteLine($"FAIL {dir}: directory not found");
        allOk = false;
        continue;
    }

    // A universal macOS bundle keeps RID-specific assemblies in MonoBundle/.xamarin/<rid>; each RID
    // loads the shared root plus its own folder, so check each combination.
    string ridRoot = Path.Combine(root, ".xamarin");
    string[] ridDirs = Directory.Exists(ridRoot) ? Directory.GetDirectories(ridRoot) : [];
    if (ridDirs.Length == 0)
    {
        allOk &= CheckDirectory(root, [root]);
    }
    else
    {
        foreach (string ridDir in ridDirs)
        {
            allOk &= CheckDirectory($"{root} [{Path.GetFileName(ridDir)}]", [root, ridDir]);
        }
    }
}

return allOk ? 0 : 1;

bool CheckDirectory(string dir, string[] sources)
{
    var assemblies = new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
    var readers = new List<(string Name, PEReader Pe)>();
    try
    {
        foreach (string path in sources.SelectMany(static d => Directory.EnumerateFiles(d, "*.dll")))
        {
            var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                continue;
            }

            MetadataReader md = pe.GetMetadataReader();
            if (!md.IsAssembly)
            {
                pe.Dispose();
                continue;
            }

            string name = md.GetString(md.GetAssemblyDefinition().Name);
            assemblies[name] = AssemblyInfo.Read(md);
            readers.Add((name, pe));
        }

        var errors = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string name, PEReader pe) in readers)
        {
            MetadataReader md = pe.GetMetadataReader();
            foreach (TypeReferenceHandle handle in md.TypeReferences)
            {
                if (!TryResolveScope(md, handle, out string? targetAssembly, out string? typeName))
                {
                    continue;
                }

                if (!assemblies.TryGetValue(targetAssembly!, out AssemblyInfo? target))
                {
                    errors.Add($"{name} references missing assembly {targetAssembly}");
                }
                else if (!target.Types.Contains(typeName!) && !target.Forwarded.Contains(typeName!))
                {
                    errors.Add($"{name} references {typeName}, missing from {targetAssembly}");
                }
            }
        }

        if (assemblies.TryGetValue("PenBridge.Core", out AssemblyInfo? core) && core.Types.Contains(PairingType))
        {
            if (!assemblies.TryGetValue(BouncyCastle, out AssemblyInfo? bc))
            {
                errors.Add($"{PairingType} is bundled but {BouncyCastle}.dll is not (J-PAKE pairing would fail)");
            }
            else
            {
                foreach (string type in jpakeTypes)
                {
                    if (!bc.Types.Contains(type))
                    {
                        errors.Add($"{PairingType} is bundled but {type} is not");
                    }
                }
            }
        }
        else if (requirePairing)
        {
            errors.Add($"--require-pairing: {PairingType} is not in the bundle");
        }

        if (errors.Count == 0)
        {
            string pairing = core is not null && core.Types.Contains(PairingType) ? "J-PAKE pairing code present" : "pairing code not referenced by the head";
            Console.WriteLine($"OK   {dir}: {assemblies.Count} assemblies, references closed, {pairing}");
            return true;
        }

        Console.WriteLine($"FAIL {dir}: {errors.Count} problem(s)");
        foreach (string error in errors)
        {
            Console.WriteLine($"       {error}");
        }

        return false;
    }
    finally
    {
        foreach ((_, PEReader pe) in readers)
        {
            pe.Dispose();
        }
    }
}

static bool TryResolveScope(MetadataReader md, TypeReferenceHandle handle, out string? assembly, out string? typeName)
{
    TypeReference tr = md.GetTypeReference(handle);
    string name = FullName(md, tr.Namespace, tr.Name);
    EntityHandle scope = tr.ResolutionScope;
    while (scope.Kind == HandleKind.TypeReference)
    {
        TypeReference outer = md.GetTypeReference((TypeReferenceHandle)scope);
        name = FullName(md, outer.Namespace, outer.Name) + "/" + name;
        scope = outer.ResolutionScope;
    }

    if (scope.Kind != HandleKind.AssemblyReference)
    {
        assembly = null;
        typeName = null;
        return false;
    }

    assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);

    // Forwarders are listed by top-level type; nested types travel with their outer type.
    int slash = name.IndexOf('/', StringComparison.Ordinal);
    typeName = slash < 0 ? name : name[..slash];
    return true;
}

static string FullName(MetadataReader md, StringHandle ns, StringHandle name) =>
    ns.IsNil || md.GetString(ns).Length == 0 ? md.GetString(name) : md.GetString(ns) + "." + md.GetString(name);

internal sealed class AssemblyInfo
{
    public HashSet<string> Types { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Forwarded { get; } = new(StringComparer.Ordinal);

    public static AssemblyInfo Read(MetadataReader md)
    {
        var info = new AssemblyInfo();
        foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
        {
            TypeDefinition td = md.GetTypeDefinition(handle);
            if (td.GetDeclaringType().IsNil)
            {
                info.Types.Add(Name(md, td.Namespace, td.Name));
            }
        }

        foreach (ExportedTypeHandle handle in md.ExportedTypes)
        {
            ExportedType et = md.GetExportedType(handle);
            if (et.Implementation.Kind == HandleKind.AssemblyReference)
            {
                info.Forwarded.Add(Name(md, et.Namespace, et.Name));
            }
        }

        return info;
    }

    private static string Name(MetadataReader md, StringHandle ns, StringHandle name) =>
        ns.IsNil || md.GetString(ns).Length == 0 ? md.GetString(name) : md.GetString(ns) + "." + md.GetString(name);
}
