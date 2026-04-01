using AwesomeAssertions;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Extensions;

namespace MMCA.Common.Domain.Tests.Extensions;

public sealed class EntityTypeExtensionsTests
{
    [IdValueGenerated]
    private sealed class EntityWithGeneratedId : AuditableBaseEntity<int>;

    private sealed class EntityWithoutGeneratedId : AuditableBaseEntity<int>;

    [Fact]
    public void IsIdValueGenerated_WhenDecoratedWithAttribute_ReturnsTrue() =>
        typeof(EntityWithGeneratedId).IsIdValueGenerated.Should().BeTrue();

    [Fact]
    public void IsIdValueGenerated_WhenNotDecorated_ReturnsFalse() =>
        typeof(EntityWithoutGeneratedId).IsIdValueGenerated.Should().BeFalse();

    [Fact]
    public void IsIdValueGenerated_ForArbitraryType_ReturnsFalse() =>
        typeof(string).IsIdValueGenerated.Should().BeFalse();
}
