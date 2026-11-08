using System.Reflection;
using System.Reflection.Metadata;

namespace SummerSync.AsmInspect;

/// <summary>
/// Checks that every type and member an assembly references exists, and is accessible, in the assemblies
/// found in a directory (the game's Managed folder).
///
/// Our net35 assemblies are compiled against the .NET 3.5 reference assemblies, but at runtime the game
/// resolves the same references by simple name against its own reduced BCL (mscorlib/System/System.Core
/// 2.0.5.0). An API that only exists in the reference assemblies compiles fine and then fails inside the
/// game with a MissingMethodException; this check turns that into a build failure.
/// </summary>
internal sealed class ReferenceVerifier : IDisposable
{
    private const int MaxHops = 16;

    private readonly string directory;
    private readonly Dictionary<string, ManagedAssembly?> assemblies = new(StringComparer.OrdinalIgnoreCase);

    public ReferenceVerifier(string directory)
    {
        this.directory = directory;
    }

    public sealed class Report
    {
        public int TypesChecked { get; set; }

        public int MembersChecked { get; set; }

        public List<string> Problems { get; } = new();

        public SortedSet<string> AssembliesUsed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedSet<string> AssembliesNotFound { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public Report Verify(ManagedAssembly subject)
    {
        var report = new Report();
        MetadataReader reader = subject.Reader;

        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            if (TypeNames.TryResolve(reader, handle, out string assembly, out string type))
            {
                LocateReferencedType(assembly, type, report, countAsChecked: true);
            }
        }

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            EntityHandle parent = member.Parent;
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                parent = TypeNames.GenericDefinitionOf(reader, (TypeSpecificationHandle)parent);
            }

            // Members of the subject's own types and of arrays need no external definition.
            if (parent.Kind != HandleKind.TypeReference
                || !TypeNames.TryResolve(reader, (TypeReferenceHandle)parent, out string assembly, out string type))
            {
                continue;
            }

            // A missing type was already reported by the type pass.
            (ManagedAssembly File, TypeDefinitionHandle Type)? located = LocateReferencedType(assembly, type, report, countAsChecked: false);
            if (located == null)
            {
                continue;
            }

            string name = reader.GetString(member.Name);
            string description;
            bool? accessible;
            if (member.GetKind() == MemberReferenceKind.Field)
            {
                string fieldType = member.DecodeFieldSignature(SignatureFormatter.Instance, null);
                description = "field " + fieldType + " " + type + "::" + name;
                accessible = FindField(located.Value.File, located.Value.Type, name, fieldType);
            }
            else
            {
                MethodSignature<string> signature = member.DecodeMethodSignature(SignatureFormatter.Instance, null);
                description = "method " + type + "::" + SignatureFormatter.Describe(name, signature);
                accessible = FindMethod(located.Value.File, located.Value.Type, name, signature);
            }

            report.MembersChecked++;
            if (accessible == null)
            {
                report.Problems.Add($"MISSING [{located.Value.File.Name} {located.Value.File.Version}] {description}");
            }
            else if (accessible == false)
            {
                report.Problems.Add($"NOT ACCESSIBLE [{located.Value.File.Name} {located.Value.File.Version}] {description}");
            }
        }

        return report;
    }

    public void Dispose()
    {
        foreach (ManagedAssembly? assembly in assemblies.Values)
        {
            assembly?.Dispose();
        }
    }

    private (ManagedAssembly File, TypeDefinitionHandle Type)? LocateReferencedType(string assembly, string type, Report report, bool countAsChecked)
    {
        if (Load(assembly) == null)
        {
            report.AssembliesNotFound.Add(assembly);
            return null;
        }

        report.AssembliesUsed.Add(assembly);
        (ManagedAssembly File, TypeDefinitionHandle Type)? located = LocateType(assembly, type);
        if (countAsChecked)
        {
            report.TypesChecked++;
            if (located == null)
            {
                report.Problems.Add($"MISSING [{assembly}] type {type}");
            }
        }

        return located;
    }

    private ManagedAssembly? Load(string name)
    {
        if (!assemblies.TryGetValue(name, out ManagedAssembly? assembly))
        {
            string path = Path.Combine(directory, name + ".dll");
            assembly = File.Exists(path) ? new ManagedAssembly(path) : null;
            assemblies[name] = assembly;
        }

        return assembly;
    }

    /// <summary>Finds a type definition, following type forwarders between assemblies of the directory.</summary>
    private (ManagedAssembly File, TypeDefinitionHandle Type)? LocateType(string assembly, string type)
    {
        for (int hop = 0; hop < MaxHops; hop++)
        {
            ManagedAssembly? file = Load(assembly);
            if (file == null)
            {
                return null;
            }

            if (file.TryGetType(type, out TypeDefinitionHandle handle))
            {
                return (file, handle);
            }

            if (!file.TryGetForwarder(type, out assembly))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Returns null when no matching method exists on the type or its base types, otherwise whether it is accessible.</summary>
    private bool? FindMethod(ManagedAssembly file, TypeDefinitionHandle type, string name, MethodSignature<string> signature)
    {
        // Past a generic base class the type arguments would need substituting, so only the shape is compared.
        bool shapeOnly = false;
        for (int hop = 0; hop < MaxHops; hop++)
        {
            TypeDefinition definition = file.Reader.GetTypeDefinition(type);
            foreach (MethodDefinitionHandle handle in definition.GetMethods())
            {
                MethodDefinition method = file.Reader.GetMethodDefinition(handle);
                if (!file.Reader.StringComparer.Equals(method.Name, name))
                {
                    continue;
                }

                MethodSignature<string> candidate = method.DecodeSignature(SignatureFormatter.Instance, null);
                bool matches = shapeOnly
                    ? candidate.Header.IsInstance == signature.Header.IsInstance && candidate.ParameterTypes.Length == signature.ParameterTypes.Length
                    : SignatureFormatter.Matches(candidate, signature);
                if (matches)
                {
                    MethodAttributes access = method.Attributes & MethodAttributes.MemberAccessMask;
                    return access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
                }
            }

            if (!TryMoveToBase(ref file, ref type, ref shapeOnly))
            {
                return null;
            }
        }

        return null;
    }

    private bool? FindField(ManagedAssembly file, TypeDefinitionHandle type, string name, string fieldType)
    {
        bool shapeOnly = false;
        for (int hop = 0; hop < MaxHops; hop++)
        {
            TypeDefinition definition = file.Reader.GetTypeDefinition(type);
            foreach (FieldDefinitionHandle handle in definition.GetFields())
            {
                FieldDefinition field = file.Reader.GetFieldDefinition(handle);
                if (file.Reader.StringComparer.Equals(field.Name, name)
                    && (shapeOnly || field.DecodeSignature(SignatureFormatter.Instance, null) == fieldType))
                {
                    FieldAttributes access = field.Attributes & FieldAttributes.FieldAccessMask;
                    return access is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;
                }
            }

            if (!TryMoveToBase(ref file, ref type, ref shapeOnly))
            {
                return null;
            }
        }

        return null;
    }

    private bool TryMoveToBase(ref ManagedAssembly file, ref TypeDefinitionHandle type, ref bool shapeOnly)
    {
        EntityHandle baseType = file.Reader.GetTypeDefinition(type).BaseType;
        if (baseType.IsNil)
        {
            return false;
        }

        if (baseType.Kind == HandleKind.TypeSpecification)
        {
            baseType = TypeNames.GenericDefinitionOf(file.Reader, (TypeSpecificationHandle)baseType);
            shapeOnly = true;
        }

        switch (baseType.Kind)
        {
            case HandleKind.TypeDefinition:
                type = (TypeDefinitionHandle)baseType;
                return true;

            case HandleKind.TypeReference when TypeNames.TryResolve(file.Reader, (TypeReferenceHandle)baseType, out string assembly, out string name):
                (ManagedAssembly File, TypeDefinitionHandle Type)? located = LocateType(assembly, name);
                if (located == null)
                {
                    return false;
                }

                file = located.Value.File;
                type = located.Value.Type;
                return true;

            default:
                return false;
        }
    }
}
