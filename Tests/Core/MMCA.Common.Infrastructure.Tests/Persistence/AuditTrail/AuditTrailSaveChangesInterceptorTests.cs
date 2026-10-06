using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Domain.Privacy;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// Coverage for <see cref="AuditTrailSaveChangesInterceptor"/>: which rows a save produces, how
/// personal data is masked, where the changing user comes from, and the two mechanical requirements
/// copied from the domain-event interceptor (retry-discard, and mutation through <c>Add</c> only).
/// Every assertion runs against a real SQLite round-trip, so what is asserted is what commits.
/// </summary>
public sealed class AuditTrailSaveChangesInterceptorTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = AuditTrailTestHarness.CreateTimeProvider();
    private readonly AuditTrailTestContext _context;

    public AuditTrailSaveChangesInterceptorTests() =>
        _context = AuditTrailTestContext.Create(_timeProvider);

    public void Dispose() => _context.Dispose();

    // ── Added: one summary row, carrying the key the insert actually assigned ──
    [Fact]
    public async Task SaveChanges_AddedEntity_WritesOneSummaryRowWithTheGeneratedKey()
    {
        var thing = new AuditedThing { Name = "First", Email = "someone@example.com", Quantity = 3 };
        _context.AuditedThings.Add(thing);

        await _context.SaveChangesAsync(userId: 42);

        var row = await SingleRowAsync();
        row.Operation.Should().Be("Added");
        row.PropertyName.Should().BeNull("a create is recorded once, not once per column");
        row.OldValue.Should().BeNull();
        row.NewValue.Should().BeNull();
        row.EntityType.Should().Be(typeof(AuditedThing).FullName);
        row.ChangedBy.Should().Be(42);
        row.ChangedOn.Should().Be(AuditTrailTestHarness.EpochUtc);
        thing.Id.Should().BeGreaterThan(0);
        row.EntityKey.Should().Be(thing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "the key is store-generated, so the row must be rewritten once the insert assigns it");
    }

    // ── Added through the synchronous path ──
    [Fact]
    public void SaveChanges_Synchronous_RecordsTheSameRowAndResolvesTheKey()
    {
        var thing = new AuditedThing { Name = "Sync" };
        _context.AuditedThings.Add(thing);

        _context.SaveChanges(userId: 7);

        var row = _context.TrailRows.AsNoTracking().Single();
        row.Operation.Should().Be("Added");
        row.ChangedBy.Should().Be(7);
        row.EntityKey.Should().Be(thing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Modified: one row per property that really changed ──
    [Fact]
    public async Task SaveChanges_ModifiedEntity_WritesOneRowPerChangedProperty()
    {
        var thing = await SeedAsync();

        thing.Name = "Renamed";
        thing.Quantity = 9;
        await _context.SaveChangesAsync(userId: 5);

        var rows = await ModifiedRowsAsync();
        rows.Should().HaveCount(2);

        var name = rows.Single(r => r.PropertyName == nameof(AuditedThing.Name));
        name.OldValue.Should().Be("Original");
        name.NewValue.Should().Be("Renamed");

        var quantity = rows.Single(r => r.PropertyName == nameof(AuditedThing.Quantity));
        quantity.OldValue.Should().Be("1");
        quantity.NewValue.Should().Be("9");

        rows.Should().NotContain(r => r.PropertyName == nameof(AuditedThing.Email),
            "a property nobody touched is not part of the change");
    }

    // ── Modified: EF flagged the property, but the value is identical ──
    [Fact]
    public async Task SaveChanges_WholeEntityMarkedModifiedButUnchanged_WritesNothing()
    {
        var thing = await SeedAsync();

        // The Update idiom flags every property as modified; none of the values moved.
        _context.Entry(thing).State = EntityState.Modified;
        await _context.SaveChangesAsync(userId: 5);

        (await ModifiedRowsAsync()).Should().BeEmpty("a trail that records non-changes is noise");
    }

    // ── Deleted: one summary row ──
    [Fact]
    public async Task SaveChanges_DeletedEntity_WritesOneSummaryRow()
    {
        var thing = await SeedAsync();

        _context.AuditedThings.Remove(thing);
        await _context.SaveChangesAsync(userId: 11);

        var rows = await RowsAsync();
        var deleted = rows.Single(r => r.Operation == "Deleted");
        deleted.PropertyName.Should().BeNull();
        deleted.EntityKey.Should().Be(thing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        deleted.ChangedBy.Should().Be(11);
    }

    // ── Composite keys ──
    [Fact]
    public async Task SaveChanges_CompositeKeyEntity_JoinsTheKeyPartsInModelOrder()
    {
        _context.CompositeKeyThings.Add(new CompositeKeyThing { PartA = 7, PartB = "beta", Note = "n" });

        await _context.SaveChangesAsync(userId: 1);

        (await SingleRowAsync()).EntityKey.Should().Be("7|beta");
    }

    // ── Entities that never opted in ──
    [Fact]
    public async Task SaveChanges_EntityWithoutTheMarker_IsNeverRecorded()
    {
        _context.PlainThings.Add(new PlainThing { Id = 1, Name = "Ignored" });
        await _context.SaveChangesAsync(userId: 1);

        var plain = await _context.PlainThings.SingleAsync();
        plain.Name = "Still ignored";
        await _context.SaveChangesAsync(userId: 1);

        (await RowsAsync()).Should().BeEmpty("the trail is opt-in per entity, and this one never opted in");
    }

    // ── The framework's own tables are never audited ──
    [Fact]
    public void IsFrameworkEntity_TheFrameworksOwnBookkeepingTypes_AreExcluded()
    {
        AuditTrailSaveChangesInterceptor.IsFrameworkEntity(typeof(AuditTrailEntry)).Should().BeTrue(
            "auditing the trail would be an unbounded feedback loop");
        AuditTrailSaveChangesInterceptor.IsFrameworkEntity(typeof(OutboxMessage)).Should().BeTrue();
        AuditTrailSaveChangesInterceptor.IsFrameworkEntity(typeof(InboxMessage)).Should().BeTrue();
        AuditTrailSaveChangesInterceptor.IsFrameworkEntity(typeof(ScheduledJobEntry)).Should().BeTrue();
        AuditTrailSaveChangesInterceptor.IsFrameworkEntity(typeof(AuditedThing)).Should().BeFalse();
    }

    // ── Changing a trail row itself produces no second-order rows ──
    [Fact]
    public async Task SaveChanges_ModifyingATrailRow_ProducesNoFurtherRows()
    {
        await SeedAsync();
        var row = await _context.TrailRows.SingleAsync();

        row.EntityKey = "rewritten";
        await _context.SaveChangesAsync(userId: 1);

        (await RowsAsync()).Should().ContainSingle("the trail must never record itself");
    }

    // ── PII is redacted at capture, on both sides ──
    [Fact]
    public async Task SaveChanges_PiiProperty_RecordsTheRedactionTokenAndNeverTheValue()
    {
        var thing = await SeedAsync();

        thing.Email = "new.address@example.com";
        await _context.SaveChangesAsync(userId: 5);

        var row = (await ModifiedRowsAsync()).Single();
        row.PropertyName.Should().Be(nameof(AuditedThing.Email));
        row.OldValue.Should().Be(PiiRedactor.RedactedToken);
        row.NewValue.Should().Be(PiiRedactor.RedactedToken);

        string?[] everyColumn =
        [
            row.EntityType, row.EntityKey, row.PropertyName, row.OldValue, row.NewValue,
            row.Operation, row.CorrelationId, row.TenantId,
        ];
        everyColumn.Should().NotContain(value => value != null
            && (value.Contains("example.com", StringComparison.Ordinal)
                || value.Contains("new.address", StringComparison.Ordinal)),
            "personal data must never reach the trail in clear text, in any column");
    }

    // ── An override of a [Pii] property that does not repeat the marker is still redacted ──
    [Fact]
    public async Task SaveChanges_PiiPropertyOverriddenWithoutTheMarker_RecordsTheRedactionToken()
    {
        var thing = new OverridingPiiThing { Email = "original@example.com" };
        _context.OverridingPiiThings.Add(thing);
        await _context.SaveChangesAsync(userId: 1);

        thing.Email = "new.address@example.com";
        await _context.SaveChangesAsync(userId: 5);

        var row = (await ModifiedRowsAsync()).Single();
        row.PropertyName.Should().Be(nameof(OverridingPiiThing.Email));
        row.OldValue.Should().Be(PiiRedactor.RedactedToken);
        row.NewValue.Should().Be(PiiRedactor.RedactedToken);
    }

    // ── The changing user comes from the save, and is null when the save carried none ──
    [Fact]
    public async Task SaveChanges_WithoutAUserId_RecordsANullChangedBy()
    {
        _context.AuditedThings.Add(new AuditedThing { Name = "System" });

        await _context.SaveChangesAsync();

        (await SingleRowAsync()).ChangedBy.Should().BeNull(
            "a background save has no identity to attribute the change to");
    }

    // ── A capture whose save never completed does not duplicate rows ──
    [Fact]
    public async Task SaveChanges_AfterAFailedSave_WritesOneRowPerChange()
    {
        _context.AuditedThings.Add(new AuditedThing { Name = "Retried" });

        _context.FailNextSave = true;
        var firstAttempt = async () => await _context.SaveChangesAsync(userId: 1);
        await firstAttempt.Should().ThrowAsync<DbUpdateException>();

        _context.FailNextSave = false;
        await _context.SaveChangesAsync(userId: 1);

        (await RowsAsync()).Should().ContainSingle("the abandoned attempt's row must be discarded, not duplicated");
    }

    // ── Same transaction as the data it describes ──
    [Fact]
    public async Task SaveChanges_InsideARolledBackTransaction_LeavesNeitherDataNorTrail()
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        _context.AuditedThings.Add(new AuditedThing { Name = "Doomed" });
        await _context.SaveChangesAsync(userId: 1);

        await transaction.RollbackAsync();

        (await _context.AuditedThings.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await RowsAsync()).Should().BeEmpty("a trail row that survives its own rollback is worse than no trail");
    }

    // ── A host that never called AddAuditTrail ──
    [Fact]
    public async Task SaveChanges_WhenTheInterceptorIsNotRegistered_RecordsNothing()
    {
        await using var context = AuditTrailTestContext.Create(_timeProvider, registerInterceptor: false);
        context.AuditedThings.Add(new AuditedThing { Name = "Untracked" });

        await context.SaveChangesAsync(userId: 1);

        (await context.TrailRows.AsNoTracking().ToListAsync()).Should().BeEmpty(
            "the context resolves the interceptor with GetService, so its absence is a silent no-op");
    }

    // -- L124: the client-stamped RowVersion is bookkeeping, not a change --
    [Fact]
    public async Task SaveChanges_ModifiedAggregate_DoesNotRecordTheRowVersionStamp()
    {
        var thing = new AuditedAggregateThing { Id = 1, Name = "Original" };
        _context.AuditedAggregateThings.Add(thing);
        await _context.SaveChangesAsync(userId: 1);

        // Same user and same (frozen) clock, so the audit stamps do not move: the only columns the
        // update writes are Name and the client-stamped RowVersion.
        thing.Name = "Renamed";
        await _context.SaveChangesAsync(userId: 1);

        var rows = await ModifiedRowsAsync();
        rows.Should().ContainSingle().Which.PropertyName.Should().Be(nameof(AuditedAggregateThing.Name));
        rows.Should().NotContain(r => r.PropertyName == "RowVersion",
            "the concurrency token is re-stamped on every update on SQLite and PostgreSQL, so recording it is noise");
    }

    // -- L124: a byte-array value is rendered readably --
    [Fact]
    public async Task SaveChanges_ChangedByteArray_RendersHex()
    {
        var thing = new AuditedAggregateThing { Id = 2, Name = "Hashed", Hash = [1, 2] };
        _context.AuditedAggregateThings.Add(thing);
        await _context.SaveChangesAsync(userId: 1);

        thing.Hash = [3, 4];
        await _context.SaveChangesAsync(userId: 2);

        var row = (await ModifiedRowsAsync()).Single(r => r.PropertyName == nameof(AuditedAggregateThing.Hash));
        row.OldValue.Should().Be("0102");
        row.NewValue.Should().Be("0304");
    }

    // -- M181: a change confined to an owned value object is recorded under its owner --
    [Fact]
    public async Task SaveChanges_OwnedPropertyChanged_WritesARowUnderTheOwner()
    {
        var thing = new AuditedThing { Name = "Owner", Address = new ThingAddress("A") };
        _context.AuditedThings.Add(thing);
        await _context.SaveChangesAsync(userId: 1);

        thing.Address = new ThingAddress("B");
        await _context.SaveChangesAsync(userId: 2);

        var row = (await ModifiedRowsAsync()).Should().ContainSingle().Subject;
        row.PropertyName.Should().Be("Address.City");
        row.OldValue.Should().Be("A");
        row.NewValue.Should().Be("B");
        row.EntityType.Should().Be(typeof(AuditedThing).FullName);
        row.EntityKey.Should().Be(thing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Helpers ──
    private async Task<AuditedThing> SeedAsync()
    {
        var thing = new AuditedThing { Name = "Original", Email = "original@example.com", Quantity = 1 };
        _context.AuditedThings.Add(thing);
        await _context.SaveChangesAsync(userId: 1);
        return thing;
    }

    private async Task<List<AuditTrailEntry>> RowsAsync() =>
        await _context.TrailRows.AsNoTracking().ToListAsync();

    private async Task<List<AuditTrailEntry>> ModifiedRowsAsync() =>
        await _context.TrailRows.AsNoTracking().Where(r => r.Operation == "Modified").ToListAsync();

    private async Task<AuditTrailEntry> SingleRowAsync() =>
        await _context.TrailRows.AsNoTracking().SingleAsync();
}
