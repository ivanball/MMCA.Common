using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.ProcessManagers;

namespace MMCA.Common.Domain.Tests.ProcessManagers;

/// <summary>
/// Tests for <see cref="ProcessManagerAggregateRoot{TIdentifierType}"/> (design item 36, rules 1 and 2):
/// the base is behaviour-only, so deriving from it adds nothing to the EF model; the last transition is
/// the last audit stamp; and a process that does not override the timeout decision ignores every timeout.
/// </summary>
public sealed class ProcessManagerAggregateRootTests
{
    private static readonly DateTime CreatedOn = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ModifiedOn = new(2026, 11, 12, 9, 15, 0, DateTimeKind.Utc);

    [Fact]
    public void LastTransition_NeverModified_IsCreatedOn()
    {
        var process = new TestProcess { Id = 1 };
        Stamp(process, nameof(AuditableBaseEntity<>.CreatedOn), CreatedOn);

        process.GetLastTransitionOn().Should().Be(
            CreatedOn,
            "a process that was never modified last transitioned when it was created");
    }

    [Fact]
    public void LastTransition_Modified_IsLastModifiedOn()
    {
        var process = new TestProcess { Id = 1 };
        Stamp(process, nameof(AuditableBaseEntity<>.CreatedOn), CreatedOn);
        Stamp(process, nameof(AuditableBaseEntity<>.LastModifiedOn), ModifiedOn);

        process.GetLastTransitionOn().Should().Be(
            ModifiedOn,
            "LastModifiedOn wins over CreatedOn once the process has been modified");
    }

    [Fact]
    public void DecideTimeout_Default_NotApplicable()
    {
        var process = new TestProcess { Id = 1 };
        var timeout = new ProcessTimeout("AnyTimeout", TimeSpan.FromMinutes(5));

        var decision = process.DecideTimeout(timeout, ModifiedOn);

        decision.Outcome.Should().Be(ProcessTimeoutOutcome.NotApplicable);
        decision.DueAt.Should().BeNull();
        decision.Errors.Should().BeEmpty();
    }

    [Fact]
    public void DeclaresNoInstanceFieldsOrProperties()
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var type = typeof(ProcessManagerAggregateRoot<>);

        type.GetFields(declared).Should().BeEmpty(
            "a field on the base would map into the EF model and the migration snapshot of every adopter");
        type.GetProperties(declared).Should().BeEmpty(
            "a property on the base would map into the EF model and the migration snapshot of every adopter");
    }

    [Fact]
    public void DerivesFromAuditableAggregateRoot()
    {
        var type = typeof(ProcessManagerAggregateRoot<int>);

        type.IsAbstract.Should().BeTrue();
        type.BaseType.Should().Be<AuditableAggregateRootEntity<int>>("the process IS the aggregate, so it keeps the aggregate root's audit fields and domain events");
    }

    private static void Stamp(TestProcess process, string propertyName, DateTime value) =>
        typeof(AuditableBaseEntity<int>)
            .GetProperty(propertyName)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(process, [value]);

    /// <summary>A process that keeps every default of the base.</summary>
    private sealed class TestProcess : ProcessManagerAggregateRoot<int>;
}
