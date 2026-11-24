using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>
/// The generic scope a signature is decoded in: the names of the enclosing type's and method's generic
/// parameters. A null list leaves the parameters positional (<c>!0</c>, <c>!!0</c>), which is how a
/// member reference to a type outside the module spells them.
/// </summary>
/// <param name="TypeParameters">The enclosing type's generic parameter names, or null when unknown.</param>
/// <param name="MethodParameters">The enclosing method's generic parameter names, or null when unknown.</param>
internal readonly record struct GenericScope(ImmutableArray<string>? TypeParameters, ImmutableArray<string>? MethodParameters)
{
    /// <summary>Gets the scope with no known names.</summary>
    internal static GenericScope None => new(null, null);
}

/// <summary>Turns the type entries of a metadata signature into <see cref="TypeSig"/> values for one module.</summary>
internal sealed class SignatureProvider(MetadataModule module) : ISignatureTypeProvider<TypeSig, GenericScope>
{
    public TypeSig GetPrimitiveType(PrimitiveTypeCode typeCode) => module.Primitive(typeCode);

    public TypeSig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        module.DefinitionSig(handle);

    public TypeSig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        module.ReferenceSig(handle);

    public TypeSig GetTypeFromSpecification(MetadataReader reader, GenericScope genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public TypeSig GetSZArrayType(TypeSig elementType) => TypeSig.CreateVector(elementType);

    public TypeSig GetArrayType(TypeSig elementType, ArrayShape shape) => TypeSig.CreateArray(elementType, shape);

    public TypeSig GetByReferenceType(TypeSig elementType) => TypeSig.CreateByReference(elementType);

    public TypeSig GetPointerType(TypeSig elementType) => TypeSig.CreatePointer(elementType);

    public TypeSig GetPinnedType(TypeSig elementType) => TypeSig.CreatePinned(elementType);

    public TypeSig GetModifiedType(TypeSig modifier, TypeSig unmodifiedType, bool isRequired) =>
        TypeSig.CreateModified(modifier, unmodifiedType, isRequired);

    public TypeSig GetGenericInstantiation(TypeSig genericType, ImmutableArray<TypeSig> typeArguments) =>
        TypeSig.CreateGenericInstance(genericType, typeArguments);

    public TypeSig GetGenericTypeParameter(GenericScope genericContext, int index) =>
        TypeSig.CreateGenericParameter(NameAt(genericContext.TypeParameters, index), index, isMethodParameter: false);

    public TypeSig GetGenericMethodParameter(GenericScope genericContext, int index) =>
        TypeSig.CreateGenericParameter(NameAt(genericContext.MethodParameters, index), index, isMethodParameter: true);

    public TypeSig GetFunctionPointerType(MethodSignature<TypeSig> signature) => TypeSig.CreateFunctionPointer(signature);

    private static string? NameAt(ImmutableArray<string>? names, int index) =>
        names is { } known && index < known.Length ? known[index] : null;
}
