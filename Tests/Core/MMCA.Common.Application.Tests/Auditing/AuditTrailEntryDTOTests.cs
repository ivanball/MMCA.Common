using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Application.Auditing;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Tests.Auditing;

/// <summary>
/// Contract coverage for the audit trail's read surface: the DTO is an immutable value, and the
/// reader's paging defaults are part of the published signature (a caller that asks for an entity's
/// history without paging must get the first page, not the whole table).
/// </summary>
public sealed class AuditTrailEntryDTOTests
{
    [Fact]
    public void AuditTrailEntryDTO_IsImmutable()
    {
        var settableProperties = typeof(AuditTrailEntryDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { } setter
                && setter.IsPublic
                && !setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            .Select(p => p.Name);

        settableProperties.Should().BeEmpty("a recorded change is history: nothing may rewrite it after the fact");
    }

    [Fact]
    public void AuditTrailEntryDTO_OptionalFields_DefaultToNull()
    {
        var dto = new AuditTrailEntryDTO
        {
            Id = Guid.NewGuid(),
            EntityType = "MMCA.Tests.Order",
            EntityKey = "1",
            Operation = "Added",
            ChangedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        dto.PropertyName.Should().BeNull("a create is recorded as one summary row without a property");
        dto.OldValue.Should().BeNull();
        dto.NewValue.Should().BeNull();
        dto.ChangedBy.Should().BeNull("a background save has no identity to attribute the change to");
        dto.CorrelationId.Should().BeNull();
    }

    [Fact]
    public void AuditTrailEntryDTO_HasValueSemantics()
    {
        var id = Guid.NewGuid();
        var changedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AuditTrailEntryDTO Create(string operation) => new()
        {
            Id = id,
            EntityType = "MMCA.Tests.Order",
            EntityKey = "1",
            Operation = operation,
            ChangedOn = changedOn,
        };

        Create("Added").Should().Be(Create("Added"));
        Create("Added").Should().NotBe(Create("Deleted"));
    }

    [Fact]
    public void IAuditTrailReader_PagingArguments_DefaultToTheFirstPage()
    {
        var parameters = typeof(IAuditTrailReader)
            .GetMethod(nameof(IAuditTrailReader.GetForEntityAsync))!
            .GetParameters();

        parameters.Single(p => p.Name == "page").DefaultValue.Should().Be(1);
        parameters.Single(p => p.Name == "pageSize").DefaultValue.Should().Be(50);
    }
}
