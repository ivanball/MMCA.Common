namespace MMCA.Common.Architecture.Tests.ForeignKeyFixtures;

/// <summary>
/// Entity and duck-typed metadata shapes for <c>DeleteBehaviorFactTests</c>. The entities are mapped
/// only by that test's own context; the metadata stand-ins answer the members the delete-behavior
/// rules read off an EF model by name.
/// </summary>
internal sealed class FkParent
{
    public int Id { get; set; }
}

internal sealed class FkChild
{
    public int Id { get; set; }

    public int? ParentId { get; set; }

    public FkParent? Parent { get; set; }
}

internal sealed class FkOtherChild
{
    public int Id { get; set; }

    public int? ParentId { get; set; }

    public FkParent? Parent { get; set; }
}

internal sealed class FkRequiredChild
{
    public int Id { get; set; }

    public int ParentId { get; set; }

    public FkParent? Parent { get; set; }
}

internal sealed class FkVersionedParent
{
    public int Id { get; set; }

    public int Version { get; set; }
}

internal sealed class FkVersionedChild
{
    public int Id { get; set; }

    public int ParentId { get; set; }

    public int ParentVersion { get; set; }

    public FkVersionedParent? Parent { get; set; }
}

internal sealed class FkOwner
{
    public int Id { get; set; }

    public FkAddress Address { get; set; } = new();
}

internal sealed class FkAddress
{
    public string Street { get; set; } = string.Empty;
}

/// <summary>Stands in for an EF model: answers <c>GetEntityTypes()</c>.</summary>
internal sealed class FakeModel(params object[] entityTypes)
{
    public object[] GetEntityTypes() => entityTypes;
}

/// <summary>Stands in for an entity type: a nullable <c>Name</c> and its declared keys.</summary>
internal sealed class FakeEntityType(string? name, params object[] foreignKeys)
{
    public string? Name => name;

    public object[] GetDeclaredForeignKeys() => foreignKeys;
}

/// <summary>An entity type with no <c>Name</c> member at all.</summary>
internal sealed class NamelessEntityType
{
    public object[] GetDeclaredForeignKeys() => [];
}

/// <summary>Stands in for a foreign key, every member nullable so the fallbacks can be reached.</summary>
internal sealed class FakeForeignKey(object[]? properties, object? deleteBehavior, object? isOwnership, FakeAnnotation? annotation)
{
    public const string SourceAnnotation = "MMCA:DeleteBehaviorSource";

    public object[]? Properties => properties;

    public object? DeleteBehavior => deleteBehavior;

    public object? IsOwnership => isOwnership;

    public FakeAnnotation? FindAnnotation(string name) =>
        string.Equals(name, SourceAnnotation, StringComparison.Ordinal) ? annotation : null;
}

/// <summary>A foreign key with no <c>FindAnnotation(string)</c> member.</summary>
internal sealed class BareForeignKey
{
    public object[] Properties => [];

    public string DeleteBehavior => "Restrict";

    public bool IsOwnership => false;
}

/// <summary>Stands in for a key property: a nullable <c>Name</c>.</summary>
internal sealed class FakeProperty(string? name)
{
    public string? Name => name;
}

/// <summary>Stands in for an annotation: a nullable <c>Value</c>.</summary>
internal sealed class FakeAnnotation(object? value)
{
    public object? Value => value;
}
