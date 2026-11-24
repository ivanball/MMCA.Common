using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>
/// One compiled assembly read straight from its file through <c>System.Reflection.Metadata</c>, with
/// nothing loaded into the runtime: every type definition (nested ones included), each type's members
/// and signatures, and each method body's IL on demand. It is the single reader behind the dependency
/// rules (<see cref="ArchitectureTypes"/>) and the rules that look inside method bodies.
/// <para>
/// A module is read once per file and kept for the life of the process: the file is copied into
/// managed memory, so no handle stays open, and every member is immutable once built, so rules running
/// in parallel share it safely.
/// </para>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A module is cached for the life of the process and its PEReader wraps an in-memory copy of the file, so there is nothing to release early.")]
internal sealed class MetadataModule
{
    private static readonly ConcurrentDictionary<string, Lazy<MetadataModule>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly PEReader _peReader;
    private readonly ConcurrentDictionary<TypeDefinitionHandle, TypeSig> _definitionSigs = new();
    private readonly ConcurrentDictionary<TypeReferenceHandle, TypeSig> _referenceSigs = new();
    private readonly ConcurrentDictionary<PrimitiveTypeCode, TypeSig> _primitives = new();
    private readonly Dictionary<MethodDefinitionHandle, MetadataMethod> _methodsByHandle = [];

    private MetadataModule(byte[] image)
    {
        _peReader = new PEReader(ImmutableArray.Create(image));
        Reader = _peReader.GetMetadataReader();
        Provider = new SignatureProvider(this);
        AssemblyName = Reader.IsAssembly ? Reader.GetString(Reader.GetAssemblyDefinition().Name) : string.Empty;

        var types = new List<MetadataType>();
        foreach (var handle in Reader.TypeDefinitions)
        {
            if (Reader.GetTypeDefinition(handle).GetDeclaringType().IsNil)
            {
                AddWithNested(new MetadataType(this, handle, declaringType: null), types);
            }
        }

        Types = types;
    }

    /// <summary>Gets the simple name of the assembly the module belongs to.</summary>
    internal string AssemblyName { get; }

    /// <summary>
    /// Gets every type the module defines, each top-level type followed depth-first by the types nested
    /// in it, in metadata order. The <c>&lt;Module&gt;</c> pseudo-type is included.
    /// </summary>
    internal IReadOnlyList<MetadataType> Types { get; }

    /// <summary>Gets the underlying metadata reader.</summary>
    internal MetadataReader Reader { get; }

    /// <summary>Gets the signature provider bound to this module.</summary>
    internal SignatureProvider Provider { get; }

    /// <summary>Reads (or returns the cached read of) the assembly file at <paramref name="path"/>.</summary>
    /// <exception cref="BadImageFormatException">The file is not a managed assembly.</exception>
    internal static MetadataModule Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var fullPath = Path.GetFullPath(path);
        return Cache.GetOrAdd(fullPath, static p => new Lazy<MetadataModule>(() => new MetadataModule(File.ReadAllBytes(p)))).Value;
    }

    /// <summary>The method a metadata token (as reflection reports it) names, or null when it is not a method of this module.</summary>
    internal MetadataMethod? FindMethod(int metadataToken)
    {
        var handle = MetadataTokens.EntityHandle(metadataToken);
        return handle.Kind == HandleKind.MethodDefinition ? FindMethod((MethodDefinitionHandle)handle) : null;
    }

    /// <summary>The method a definition handle names, or null when it is not a method of this module.</summary>
    internal MetadataMethod? FindMethod(MethodDefinitionHandle handle) => _methodsByHandle.GetValueOrDefault(handle);

    /// <summary>Records a method while the module's types are being built.</summary>
    internal void Register(MetadataMethod method) => _methodsByHandle[method.Handle] = method;

    /// <summary>Gets the shared signature of a primitive type.</summary>
    internal TypeSig Primitive(PrimitiveTypeCode code) => _primitives.GetOrAdd(code, TypeSig.CreatePrimitive);

    /// <summary>Gets the signature naming one of this module's own type definitions.</summary>
    internal TypeSig DefinitionSig(TypeDefinitionHandle handle) =>
        _definitionSigs.GetOrAdd(handle, h =>
        {
            var definition = Reader.GetTypeDefinition(h);
            var declaring = definition.GetDeclaringType();
            return TypeSig.CreateNamed(
                Reader.GetString(definition.Namespace),
                Reader.GetString(definition.Name),
                declaring.IsNil ? null : DefinitionSig(declaring),
                h);
        });

    /// <summary>Gets the signature naming a type this module references from elsewhere.</summary>
    internal TypeSig ReferenceSig(TypeReferenceHandle handle) =>
        _referenceSigs.GetOrAdd(handle, h =>
        {
            var reference = Reader.GetTypeReference(h);
            var declaring = reference.ResolutionScope.Kind == HandleKind.TypeReference
                ? ReferenceSig((TypeReferenceHandle)reference.ResolutionScope)
                : null;
            return TypeSig.CreateNamed(Reader.GetString(reference.Namespace), Reader.GetString(reference.Name), declaring, default);
        });

    /// <summary>Decodes a TypeDef, TypeRef or TypeSpec handle in the given generic scope.</summary>
    internal TypeSig DecodeType(EntityHandle handle, GenericScope scope)
    {
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            return DefinitionSig((TypeDefinitionHandle)handle);
        }

        if (handle.Kind == HandleKind.TypeReference)
        {
            return ReferenceSig((TypeReferenceHandle)handle);
        }

        if (handle.Kind == HandleKind.TypeSpecification)
        {
            return Reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(Provider, scope);
        }

        throw new InvalidOperationException($"Handle kind {handle.Kind} does not name a type.");
    }

    /// <summary>Gets the names of a type's or method's generic parameters.</summary>
    internal ImmutableArray<string> GenericParameterNames(GenericParameterHandleCollection parameters) =>
        [.. parameters.Select(p => Reader.GetString(Reader.GetGenericParameter(p).Name))];

    /// <summary>The attribute type of each custom attribute in the collection.</summary>
    internal ImmutableArray<MetadataAttribute> Attributes(CustomAttributeHandleCollection attributes) =>
        [.. attributes.Select(h => new MetadataAttribute(this, h, AttributeType(Reader.GetCustomAttribute(h).Constructor)))];

    /// <summary>The generic-parameter constraint types of a type or method.</summary>
    internal ImmutableArray<TypeSig> Constraints(GenericParameterHandleCollection parameters, GenericScope scope) =>
        [
            .. parameters
                .SelectMany(p => Reader.GetGenericParameter(p).GetConstraints())
                .Select(c => DecodeType(Reader.GetGenericParameterConstraint(c).Type, scope)),
        ];

    /// <summary>Resolves the operand of a <c>call</c>, <c>newobj</c>, <c>ldftn</c> or method <c>ldtoken</c>.</summary>
    internal MethodOperand ResolveMethod(EntityHandle handle, GenericScope bodyScope)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = _methodsByHandle[(MethodDefinitionHandle)handle];
            return new MethodOperand(method.DeclaringType.Sig, method.Name, method.ParameterTypes, [], method.DeclaringType.Handle);
        }

        if (handle.Kind == HandleKind.MemberReference)
        {
            var reference = Reader.GetMemberReference((MemberReferenceHandle)handle);
            var (declaringType, declaringDefinition, scope) = MemberParent(reference.Parent, bodyScope);
            var signature = reference.DecodeMethodSignature(Provider, scope);
            return new MethodOperand(declaringType, Reader.GetString(reference.Name), signature.ParameterTypes, [], declaringDefinition);
        }

        if (handle.Kind == HandleKind.MethodSpecification)
        {
            var specification = Reader.GetMethodSpecification((MethodSpecificationHandle)handle);
            var element = ResolveMethod(specification.Method, bodyScope);
            return element with { GenericArguments = specification.DecodeSignature(Provider, bodyScope), IsGenericInstance = true };
        }

        throw new InvalidOperationException($"Handle kind {handle.Kind} does not name a method.");
    }

    /// <summary>Resolves the operand of a field instruction or a field <c>ldtoken</c>.</summary>
    internal FieldOperand ResolveField(EntityHandle handle, GenericScope bodyScope)
    {
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            var field = Reader.GetFieldDefinition((FieldDefinitionHandle)handle);
            var declaring = field.GetDeclaringType();
            return new FieldOperand(DefinitionSig(declaring), Reader.GetString(field.Name), declaring);
        }

        var reference = Reader.GetMemberReference((MemberReferenceHandle)handle);
        var (declaringType, declaringDefinition, _) = MemberParent(reference.Parent, bodyScope);
        return new FieldOperand(declaringType, Reader.GetString(reference.Name), declaringDefinition);
    }

    /// <summary>
    /// Reads the first constructor argument of a custom attribute when it is a <see cref="Type"/> or a
    /// string (both serialize as a string), or returns null.
    /// </summary>
    internal string? LeadingStringArgument(CustomAttributeHandle handle)
    {
        var blob = Reader.GetBlobReader(Reader.GetCustomAttribute(handle).Value);
        if (blob.Length < 3 || blob.ReadUInt16() != 1)
        {
            return null;
        }

        try
        {
            return blob.ReadSerializedString();
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Reads a method body.</summary>
    internal MethodBodyBlock GetMethodBody(int relativeVirtualAddress) => _peReader.GetMethodBody(relativeVirtualAddress);

    private void AddWithNested(MetadataType type, List<MetadataType> types)
    {
        types.Add(type);

        foreach (var nestedHandle in Reader.GetTypeDefinition(type.Handle).GetNestedTypes())
        {
            var nested = new MetadataType(this, nestedHandle, type);
            type.AddNested(nested);
            AddWithNested(nested, types);
        }
    }

    /// <summary>
    /// The declaring type of a member reference, the definition it resolves to inside this module (nil
    /// otherwise), and the generic scope its own signature is read in: the declaring type's parameter
    /// names when that type is defined here, positional names when it is not.
    /// </summary>
    private (TypeSig DeclaringType, TypeDefinitionHandle Definition, GenericScope Scope) MemberParent(EntityHandle parent, GenericScope bodyScope)
    {
        if (parent.Kind == HandleKind.TypeDefinition)
        {
            var definition = (TypeDefinitionHandle)parent;
            return (DefinitionSig(definition), definition, ScopeOf(definition));
        }

        if (parent.Kind == HandleKind.TypeReference)
        {
            return (ReferenceSig((TypeReferenceHandle)parent), default, GenericScope.None);
        }

        if (parent.Kind == HandleKind.TypeSpecification)
        {
            var specification = DecodeType(parent, bodyScope);
            var element = specification.Kind == TypeSigKind.GenericInstance ? specification.Element! : specification;
            return (specification, default, element.Definition.IsNil ? GenericScope.None : ScopeOf(element.Definition));
        }

        if (parent.Kind == HandleKind.MethodDefinition)
        {
            var owner = Reader.GetMethodDefinition((MethodDefinitionHandle)parent).GetDeclaringType();
            return (DefinitionSig(owner), owner, ScopeOf(owner));
        }

        return (TypeSig.CreateNamed(string.Empty, "<Module>", declaring: null, default), default, GenericScope.None);
    }

    private GenericScope ScopeOf(TypeDefinitionHandle handle) =>
        new(GenericParameterNames(Reader.GetTypeDefinition(handle).GetGenericParameters()), null);

    private TypeSig AttributeType(EntityHandle constructor) =>
        constructor.Kind == HandleKind.MethodDefinition
            ? DefinitionSig(Reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType())
            : MemberParent(Reader.GetMemberReference((MemberReferenceHandle)constructor).Parent, GenericScope.None).DeclaringType;
}
