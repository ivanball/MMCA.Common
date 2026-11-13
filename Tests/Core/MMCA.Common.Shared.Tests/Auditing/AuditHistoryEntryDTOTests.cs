using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Auditing;

namespace MMCA.Common.Shared.Tests.Auditing;

/// <summary>
/// Contract coverage for the audit trail's read surface: the DTO is an immutable value with the
/// camelCase wire shape the audit-history endpoint and its UI panel share.
/// </summary>
public sealed class AuditHistoryEntryDTOTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AuditHistoryEntryDTO_IsImmutable()
    {
        var settableProperties = typeof(AuditHistoryEntryDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { } setter
                && setter.IsPublic
                && !setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            .Select(p => p.Name);

        settableProperties.Should().BeEmpty("a recorded change is history: nothing may rewrite it after the fact");
    }

    [Fact]
    public void AuditHistoryEntryDTO_OptionalFields_DefaultToNull()
    {
        var dto = new AuditHistoryEntryDTO
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
    public void AuditHistoryEntryDTO_HasValueSemantics()
    {
        var id = Guid.NewGuid();
        var changedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AuditHistoryEntryDTO Create(string operation) => new()
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
    public void SerializesCamelCase_AllMembers()
    {
        var dto = new AuditHistoryEntryDTO
        {
            Id = new Guid(0x6f9619ff, 0x8b86, 0xd011, 0xb4, 0x2d, 0x00, 0xc0, 0x4f, 0xc9, 0x64, 0xff),
            EntityType = "MMCA.Tests.Order",
            EntityKey = "42",
            PropertyName = "Status",
            OldValue = "Open",
            NewValue = "Closed",
            Operation = "Modified",
            ChangedBy = 7,
            ChangedOn = new DateTime(2026, 11, 11, 9, 30, 0, DateTimeKind.Utc),
            CorrelationId = "corr-1",
        };

        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(dto, WebOptions));
        JsonElement root = json.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            [
                "id", "entityType", "entityKey", "propertyName", "oldValue", "newValue",
                "operation", "changedBy", "changedOn", "correlationId",
            ],
            "the UI panel reads exactly these camelCase members");
        root.GetProperty("entityKey").GetString().Should().Be("42");
        root.GetProperty("operation").GetString().Should().Be("Modified");
        root.GetProperty("changedBy").GetInt32().Should().Be(7);
        root.GetProperty("changedOn").GetDateTime().Should().Be(dto.ChangedOn);
    }

    [Fact]
    public void RequiredMembers_AreTheRecordedFacts()
    {
        var required = typeof(AuditHistoryEntryDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttributes().Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
            .Select(p => p.Name);

        required.Should().BeEquivalentTo(
            ["Id", "EntityType", "EntityKey", "Operation", "ChangedOn"],
            "the moved type keeps the same required set as the Application DTO it replaces");
    }
}
