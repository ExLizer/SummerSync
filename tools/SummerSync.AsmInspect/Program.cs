using System.Reflection;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using SummerSync.AsmInspect;

const string Usage = """
    asminspect - static metadata inspection of managed assemblies (nothing is loaded or executed)

    asminspect refs <assembly>
        Assembly identity and the assemblies it references.
    asminspect types <assembly> [--filter <regex>] [--public]
        Types with their kind and base type.
    asminspect members <assembly> <type-full-name> [--public]
        Fields and methods of one type, with signatures. Nested types are written Outer/Inner.
    asminspect find <assembly> <regex>
        Members whose "Type::Member" name matches.
    asminspect verify-refs --against <directory> <assembly>...
        Fails (exit code 1) when a referenced type or member is missing or not accessible in the
        assemblies of <directory>. Used to check our net35 assemblies against the game's Managed folder.
    """;

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    switch (args[0])
    {
        case "refs":
            return Refs(args[1]);
        case "types":
            return Types(args[1], Option(args, "--filter"), args.Contains("--public"));
        case "members" when args.Length >= 3:
            return Members(args[1], args[2], args.Contains("--public"));
        case "find" when args.Length >= 3:
            return Find(args[1], args[2]);
        case "verify-refs":
            return VerifyRefs(Option(args, "--against"), Positional(args.Skip(1), "--against"));
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception exception) when (exception is IOException or InvalidDataException or BadImageFormatException or ArgumentException)
{
    // Set ASMINSPECT_TRACE=1 to see where a malformed assembly tripped the reader.
    Console.Error.WriteLine(Environment.GetEnvironmentVariable("ASMINSPECT_TRACE") == "1" ? exception.ToString() : "error: " + exception.Message);
    return 2;
}

static string? Option(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static List<string> Positional(IEnumerable<string> args, params string[] optionsWithValue)
{
    var result = new List<string>();
    bool skipNext = false;
    foreach (string arg in args)
    {
        if (skipNext)
        {
            skipNext = false;
        }
        else if (optionsWithValue.Contains(arg))
        {
            skipNext = true;
        }
        else if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            result.Add(arg);
        }
    }

    return result;
}

static int Refs(string path)
{
    using var assembly = new ManagedAssembly(path);
    MetadataReader reader = assembly.Reader;
    Console.WriteLine($"{assembly.Name} {assembly.Version}  (metadata {reader.MetadataVersion}, {reader.TypeDefinitions.Count} types, {reader.MethodDefinitions.Count} methods)");
    foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
    {
        AssemblyReference reference = reader.GetAssemblyReference(handle);
        string token = Convert.ToHexString(reader.GetBlobBytes(reference.PublicKeyOrToken)).ToLowerInvariant();
        Console.WriteLine($"  -> {reader.GetString(reference.Name)} {reference.Version}  token={(token.Length == 0 ? "none" : token)}");
    }

    return 0;
}

static int Types(string path, string? filter, bool publicOnly)
{
    using var assembly = new ManagedAssembly(path);
    MetadataReader reader = assembly.Reader;
    Regex? regex = filter == null ? null : new Regex(filter, RegexOptions.IgnoreCase);
    var lines = new List<string>();
    foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = TypeNames.Of(reader, handle);
        if (name == "<Module>" || (publicOnly && !IsPublic(type.Attributes)) || (regex != null && !regex.IsMatch(name)))
        {
            continue;
        }

        string baseType = BaseTypeName(reader, type);
        lines.Add($"{TypeAccess(type.Attributes),-9} {TypeKind(type.Attributes, baseType),-9} {name}{(baseType.Length == 0 || baseType == "System.Object" ? "" : " : " + baseType)}");
    }

    lines.Sort(StringComparer.Ordinal);
    lines.ForEach(Console.WriteLine);
    Console.WriteLine($"-- {lines.Count} types in {assembly.Name} {assembly.Version}");
    return 0;
}

static int Members(string path, string typeName, bool publicOnly)
{
    using var assembly = new ManagedAssembly(path);
    MetadataReader reader = assembly.Reader;
    if (!assembly.TryGetType(typeName, out TypeDefinitionHandle handle))
    {
        Console.Error.WriteLine($"error: type '{typeName}' not found in {assembly.Name}. Use 'types --filter' to search.");
        return 1;
    }

    TypeDefinition type = reader.GetTypeDefinition(handle);
    string baseType = BaseTypeName(reader, type);
    Console.WriteLine($"{TypeAccess(type.Attributes)} {TypeKind(type.Attributes, baseType)} {typeName}{(baseType.Length == 0 ? "" : " : " + baseType)}   [{assembly.Name} {assembly.Version}]");

    Console.WriteLine("fields:");
    foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
    {
        FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
        FieldAttributes access = field.Attributes & FieldAttributes.FieldAccessMask;
        if (publicOnly && access is not (FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem))
        {
            continue;
        }

        string modifiers = (field.Attributes.HasFlag(FieldAttributes.Static) ? "static " : "") + (field.Attributes.HasFlag(FieldAttributes.Literal) ? "const " : "");
        Console.WriteLine($"  {FieldAccess(access),-18} {modifiers}{field.DecodeSignature(SignatureFormatter.Instance, null)} {reader.GetString(field.Name)}");
    }

    Console.WriteLine("methods:");
    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
    {
        MethodDefinition method = reader.GetMethodDefinition(methodHandle);
        MethodAttributes access = method.Attributes & MethodAttributes.MemberAccessMask;
        if (publicOnly && access is not (MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem))
        {
            continue;
        }

        string modifiers = (method.Attributes.HasFlag(MethodAttributes.Abstract) ? "abstract " : method.Attributes.HasFlag(MethodAttributes.Virtual) ? "virtual " : "");
        Console.WriteLine($"  {MethodAccess(access),-18} {modifiers}{SignatureFormatter.Describe(reader.GetString(method.Name), method.DecodeSignature(SignatureFormatter.Instance, null))}");
    }

    return 0;
}

static int Find(string path, string pattern)
{
    using var assembly = new ManagedAssembly(path);
    MetadataReader reader = assembly.Reader;
    var regex = new Regex(pattern, RegexOptions.IgnoreCase);
    int count = 0;
    foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string typeName = TypeNames.Of(reader, handle);
        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            if (regex.IsMatch(typeName + "::" + name))
            {
                Console.WriteLine($"{typeName}::{SignatureFormatter.Describe(name, method.DecodeSignature(SignatureFormatter.Instance, null))}");
                count++;
            }
        }

        foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
            string name = reader.GetString(field.Name);
            if (regex.IsMatch(typeName + "::" + name))
            {
                Console.WriteLine($"{typeName}::{name} : {field.DecodeSignature(SignatureFormatter.Instance, null)}");
                count++;
            }
        }
    }

    Console.WriteLine($"-- {count} members in {assembly.Name} {assembly.Version}");
    return 0;
}

static int VerifyRefs(string? directory, List<string> paths)
{
    if (directory == null || paths.Count == 0)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    if (!Directory.Exists(directory))
    {
        Console.Error.WriteLine($"error: directory '{directory}' does not exist.");
        return 2;
    }

    using var verifier = new ReferenceVerifier(directory);
    int problems = 0;
    foreach (string path in paths)
    {
        using var subject = new ManagedAssembly(path);
        ReferenceVerifier.Report report = verifier.Verify(subject);
        problems += report.Problems.Count;
        Console.WriteLine($"{Path.GetFileName(path)}: {report.TypesChecked} types and {report.MembersChecked} members checked against [{string.Join(", ", report.AssembliesUsed)}] -> {(report.Problems.Count == 0 ? "OK" : report.Problems.Count + " problem(s)")}");
        if (report.AssembliesNotFound.Count > 0)
        {
            Console.WriteLine($"  not checked (no such assembly in the directory): {string.Join(", ", report.AssembliesNotFound)}");
        }

        foreach (string problem in report.Problems)
        {
            Console.WriteLine("  " + problem);
        }
    }

    return problems == 0 ? 0 : 1;
}

static bool IsPublic(TypeAttributes attributes) =>
    (attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem;

static string TypeAccess(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
{
    TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
    TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => "protected",
    TypeAttributes.NestedPrivate => "private",
    _ => "internal",
};

static string TypeKind(TypeAttributes attributes, string baseType) => baseType switch
{
    _ when attributes.HasFlag(TypeAttributes.Interface) => "interface",
    "System.Enum" => "enum",
    "System.ValueType" => "struct",
    "System.MulticastDelegate" => "delegate",
    _ => attributes.HasFlag(TypeAttributes.Abstract) ? (attributes.HasFlag(TypeAttributes.Sealed) ? "static" : "abstract") : "class",
};

// Interfaces and System.Object have a nil base type, which still reports Kind == TypeDefinition.
static string BaseTypeName(MetadataReader reader, TypeDefinition type) => type.BaseType.IsNil ? "" : type.BaseType.Kind switch
{
    HandleKind.TypeDefinition => TypeNames.Of(reader, (TypeDefinitionHandle)type.BaseType),
    HandleKind.TypeReference => TypeNames.Of(reader, (TypeReferenceHandle)type.BaseType),
    HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)type.BaseType).DecodeSignature(SignatureFormatter.Instance, null),
    _ => "",
};

static string MethodAccess(MethodAttributes access) => access switch
{
    MethodAttributes.Public => "public",
    MethodAttributes.Family => "protected",
    MethodAttributes.FamORAssem => "protected internal",
    MethodAttributes.Assembly => "internal",
    MethodAttributes.FamANDAssem => "private protected",
    _ => "private",
};

static string FieldAccess(FieldAttributes access) => access switch
{
    FieldAttributes.Public => "public",
    FieldAttributes.Family => "protected",
    FieldAttributes.FamORAssem => "protected internal",
    FieldAttributes.Assembly => "internal",
    FieldAttributes.FamANDAssem => "private protected",
    _ => "private",
};
