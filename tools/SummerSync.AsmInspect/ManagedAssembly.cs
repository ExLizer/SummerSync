using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SummerSync.AsmInspect;

/// <summary>A managed assembly opened read-only as metadata. Nothing is loaded into the runtime.</summary>
internal sealed class ManagedAssembly : IDisposable
{
    private readonly PEReader peReader;
    private readonly Dictionary<string, TypeDefinitionHandle> typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> forwardedTypes = new(StringComparer.Ordinal);

    public ManagedAssembly(string path)
    {
        FilePath = path;
        peReader = new PEReader(File.OpenRead(path));
        if (!peReader.HasMetadata)
        {
            peReader.Dispose();
            throw new InvalidDataException($"'{path}' is not a managed assembly.");
        }

        Reader = peReader.GetMetadataReader();
        Name = Reader.IsAssembly ? Reader.GetString(Reader.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(path);

        foreach (TypeDefinitionHandle handle in Reader.TypeDefinitions)
        {
            typesByName[TypeNames.Of(Reader, handle)] = handle;
        }

        foreach (ExportedTypeHandle handle in Reader.ExportedTypes)
        {
            ExportedType exported = Reader.GetExportedType(handle);
            if (exported.IsForwarder && exported.Implementation.Kind == HandleKind.AssemblyReference)
            {
                string target = Reader.GetString(Reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
                forwardedTypes[TypeNames.Qualify(Reader.GetString(exported.Namespace), Reader.GetString(exported.Name))] = target;
            }
        }
    }

    public string FilePath { get; }

    public string Name { get; }

    public MetadataReader Reader { get; }

    public Version Version => Reader.IsAssembly ? Reader.GetAssemblyDefinition().Version : new Version();

    public bool TryGetType(string fullName, out TypeDefinitionHandle handle) => typesByName.TryGetValue(fullName, out handle);

    /// <summary>Name of the assembly a type was forwarded to, if this assembly only forwards it.</summary>
    public bool TryGetForwarder(string fullName, out string targetAssembly)
    {
        // Nested types are forwarded together with their outermost declaring type.
        int nested = fullName.IndexOf('/');
        return forwardedTypes.TryGetValue(nested < 0 ? fullName : fullName[..nested], out targetAssembly!);
    }

    public void Dispose() => peReader.Dispose();
}
