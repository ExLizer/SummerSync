using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace SummerSync.AsmInspect;

/// <summary>
/// Canonical, assembly-independent type names ("Namespace.Outer/Inner") so a reference in one assembly
/// can be compared with a definition in another.
/// </summary>
internal static class TypeNames
{
    public static string Qualify(string @namespace, string name) => @namespace.Length == 0 ? name : @namespace + "." + name;

    public static string Of(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name))
            : Of(reader, declaring) + "/" + reader.GetString(type.Name);
    }

    public static string Of(MetadataReader reader, TypeReferenceHandle handle) =>
        TryResolve(reader, handle, out _, out string fullName) ? fullName : reader.GetString(reader.GetTypeReference(handle).Name);

    /// <summary>Resolves a type reference to the simple name of its assembly and its canonical type name.</summary>
    public static bool TryResolve(MetadataReader reader, TypeReferenceHandle handle, out string assembly, out string fullName)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        switch (reference.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                fullName = Qualify(reader.GetString(reference.Namespace), name);
                return true;

            case HandleKind.TypeReference when TryResolve(reader, (TypeReferenceHandle)reference.ResolutionScope, out assembly, out string outer):
                fullName = outer + "/" + name;
                return true;

            default:
                assembly = "";
                fullName = Qualify(reader.GetString(reference.Namespace), name);
                return false;
        }
    }

    /// <summary>The generic type definition behind an instantiation such as List&lt;int&gt;, or nil for arrays and other specs.</summary>
    public static EntityHandle GenericDefinitionOf(MetadataReader reader, TypeSpecificationHandle handle)
    {
        BlobReader blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return default;
        }

        return blob.ReadSignatureTypeCode() == SignatureTypeCode.TypeHandle ? blob.ReadTypeHandle() : default;
    }
}

/// <summary>Decodes signatures into canonical strings. Custom modifiers and pinning are ignored.</summary>
internal sealed class SignatureFormatter : ISignatureTypeProvider<string, object?>
{
    public static readonly SignatureFormatter Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeNames.Of(reader, handle);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeNames.Of(reader, handle);

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "fnptr(" + string.Join(",", signature.ParameterTypes) + ")->" + signature.ReturnType;

    public static string Describe(string name, MethodSignature<string> signature) =>
        (signature.Header.IsInstance ? "instance " : "static ") + signature.ReturnType + " " + name
        + (signature.GenericParameterCount > 0 ? "`" + signature.GenericParameterCount : "")
        + "(" + string.Join(",", signature.ParameterTypes) + ")";

    public static bool Matches(MethodSignature<string> left, MethodSignature<string> right) =>
        left.Header.IsInstance == right.Header.IsInstance
        && left.GenericParameterCount == right.GenericParameterCount
        && left.ReturnType == right.ReturnType
        && left.ParameterTypes.SequenceEqual(right.ParameterTypes);
}
