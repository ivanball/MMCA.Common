using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>One type definition of a <see cref="MetadataModule"/> with the members the rules inspect.</summary>
internal sealed class MetadataType
{
    private const string CompilerGeneratedAttributeFullName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
    private const string GeneratedCodeAttributeFullName = "System.CodeDom.Compiler.GeneratedCodeAttribute";

    private readonly List<MetadataType> _nestedTypes = [];

    internal MetadataType(MetadataModule module, TypeDefinitionHandle handle, MetadataType? declaringType)
    {
        var reader = module.Reader;
        var definition = reader.GetTypeDefinition(handle);
        var attributes = definition.Attributes;

        Handle = handle;
        DeclaringType = declaringType;
        Sig = module.DefinitionSig(handle);
        IsInterface = attributes.HasFlag(TypeAttributes.Interface);
        IsAbstract = attributes.HasFlag(TypeAttributes.Abstract);
        GenericParameterNames = module.GenericParameterNames(definition.GetGenericParameters());

        var scope = new GenericScope(GenericParameterNames, null);
        BaseType = definition.BaseType.IsNil ? null : module.DecodeType(definition.BaseType, scope);
        Interfaces = [.. definition.GetInterfaceImplementations()
            .Select(i => module.DecodeType(reader.GetInterfaceImplementation(i).Interface, scope))];
        Attributes = module.Attributes(definition.GetCustomAttributes());
        Constraints = module.Constraints(definition.GetGenericParameters(), scope);
        Fields = [.. definition.GetFields().Select(f => new MetadataField(module, reader.GetFieldDefinition(f), scope))];
        Properties = [.. definition.GetProperties().Select(p => new MetadataProperty(module, reader.GetPropertyDefinition(p), scope))];
        Methods = [.. definition.GetMethods().Select(m => new MetadataMethod(module, this, m))];
        Events = [.. definition.GetEvents().Select(e => new MetadataEvent(module, reader.GetEventDefinition(e), this, scope))];
        IsCompilerGenerated = Attributes.Any(a => a.FullName is CompilerGeneratedAttributeFullName or GeneratedCodeAttributeFullName);
    }

    /// <summary>Gets the type's definition handle.</summary>
    internal TypeDefinitionHandle Handle { get; }

    /// <summary>Gets the signature naming this type.</summary>
    internal TypeSig Sig { get; }

    /// <summary>Gets the metadata name (generic arity marker included).</summary>
    internal string Name => Sig.Name;

    /// <summary>Gets the full metadata name, nested types separated by <c>/</c>.</summary>
    internal string FullName => Sig.FullName;

    /// <summary>Gets the full name as reflection spells it, nested types separated by <c>+</c>.</summary>
    internal string ReflectionFullName => FullName.Replace('/', '+');

    /// <summary>Gets the type this one is nested in.</summary>
    internal MetadataType? DeclaringType { get; }

    /// <summary>Gets the types directly nested in this one.</summary>
    internal IReadOnlyList<MetadataType> NestedTypes => _nestedTypes;

    /// <summary>Gets the base type, or null for an interface and for <c>System.Object</c>.</summary>
    internal TypeSig? BaseType { get; }

    /// <summary>Gets the directly implemented interfaces.</summary>
    internal ImmutableArray<TypeSig> Interfaces { get; }

    /// <summary>Gets the custom attributes declared on the type.</summary>
    internal ImmutableArray<MetadataAttribute> Attributes { get; }

    /// <summary>Gets the constraint types of the type's generic parameters.</summary>
    internal ImmutableArray<TypeSig> Constraints { get; }

    /// <summary>Gets the names of the type's generic parameters.</summary>
    internal ImmutableArray<string> GenericParameterNames { get; }

    /// <summary>Gets a value indicating whether the type declares generic parameters.</summary>
    internal bool HasGenericParameters => !GenericParameterNames.IsEmpty;

    /// <summary>Gets the declared fields.</summary>
    internal ImmutableArray<MetadataField> Fields { get; }

    /// <summary>Gets the declared properties.</summary>
    internal ImmutableArray<MetadataProperty> Properties { get; }

    /// <summary>Gets the declared events.</summary>
    internal ImmutableArray<MetadataEvent> Events { get; }

    /// <summary>Gets the declared methods, constructors and accessors included.</summary>
    internal ImmutableArray<MetadataMethod> Methods { get; }

    /// <summary>Gets a value indicating whether the type is an interface.</summary>
    internal bool IsInterface { get; }

    /// <summary>Gets a value indicating whether the type is anything but an interface (structs, enums and delegates included).</summary>
    internal bool IsClass => !IsInterface;

    /// <summary>Gets a value indicating whether the type is abstract (interfaces and static classes included).</summary>
    internal bool IsAbstract { get; }

    /// <summary>Gets a value indicating whether the compiler or a code generator wrote the type.</summary>
    internal bool IsCompilerGenerated { get; }

    /// <summary>True when the type carries an attribute with the given full metadata name.</summary>
    internal bool HasAttribute(string attributeFullName) =>
        Attributes.Any(a => string.Equals(a.FullName, attributeFullName, StringComparison.Ordinal));

    /// <summary>Records a nested type while the module is being built.</summary>
    internal void AddNested(MetadataType nested) => _nestedTypes.Add(nested);
}

/// <summary>One custom attribute: its type, and on demand its first constructor argument.</summary>
internal sealed class MetadataAttribute(MetadataModule module, CustomAttributeHandle handle, TypeSig attributeType)
{
    /// <summary>Gets the attribute type.</summary>
    internal TypeSig AttributeType { get; } = attributeType;

    /// <summary>Gets the attribute type's full metadata name.</summary>
    internal string FullName => AttributeType.FullName;

    /// <summary>
    /// Gets the first constructor argument when it is a type or a string, as serialized: a type comes
    /// back as its reflection name (<c>Namespace.Outer+Inner</c>), possibly assembly-qualified.
    /// </summary>
    internal string? LeadingStringArgument => module.LeadingStringArgument(handle);
}

/// <summary>One field definition.</summary>
internal sealed class MetadataField
{
    internal MetadataField(MetadataModule module, FieldDefinition definition, GenericScope scope)
    {
        Name = module.Reader.GetString(definition.Name);
        Type = definition.DecodeSignature(module.Provider, scope);
        IsStatic = definition.Attributes.HasFlag(FieldAttributes.Static);
        Attributes = module.Attributes(definition.GetCustomAttributes());
    }

    /// <summary>Gets the field name.</summary>
    internal string Name { get; }

    /// <summary>Gets the field type.</summary>
    internal TypeSig Type { get; }

    /// <summary>Gets a value indicating whether the field is static.</summary>
    internal bool IsStatic { get; }

    /// <summary>Gets the custom attributes declared on the field.</summary>
    internal ImmutableArray<MetadataAttribute> Attributes { get; }
}

/// <summary>One property definition.</summary>
internal sealed class MetadataProperty
{
    internal MetadataProperty(MetadataModule module, PropertyDefinition definition, GenericScope scope)
    {
        Type = definition.DecodeSignature(module.Provider, scope).ReturnType;
        Attributes = module.Attributes(definition.GetCustomAttributes());
    }

    /// <summary>Gets the property type.</summary>
    internal TypeSig Type { get; }

    /// <summary>Gets the custom attributes declared on the property.</summary>
    internal ImmutableArray<MetadataAttribute> Attributes { get; }
}

/// <summary>One event definition.</summary>
internal sealed class MetadataEvent
{
    internal MetadataEvent(MetadataModule module, EventDefinition definition, MetadataType declaringType, GenericScope scope)
    {
        Type = module.DecodeType(definition.Type, scope);
        Attributes = module.Attributes(definition.GetCustomAttributes());
        OtherMethods = [.. definition.GetAccessors().Others
            .Select(module.FindMethod)
            .OfType<MetadataMethod>()
            .Where(m => m.DeclaringType == declaringType)];
    }

    /// <summary>Gets the event handler type.</summary>
    internal TypeSig Type { get; }

    /// <summary>Gets the custom attributes declared on the event.</summary>
    internal ImmutableArray<MetadataAttribute> Attributes { get; }

    /// <summary>Gets the accessors other than add, remove and raise.</summary>
    internal ImmutableArray<MetadataMethod> OtherMethods { get; }
}

/// <summary>One parameter of a method definition (the return value excluded).</summary>
/// <param name="Type">The parameter type.</param>
/// <param name="Attributes">The custom attributes declared on the parameter.</param>
internal sealed record MetadataParameter(TypeSig Type, ImmutableArray<MetadataAttribute> Attributes);
