using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conversions;

/// <summary>
/// The persistence half of ADR-115: the pre-convention registration on
/// <c>ApplicationDbContext.ConfigureConventions</c> reaches every property of a wrapped type on
/// every engine, the backing column stays the primitive, and a wrapped key still generates the way
/// its primitive did.
/// <para>
/// The engine assertions are model-only (nothing connects). The behavioural proof is the SQLite
/// round trip, which inserts through a real database and reads the generated key back.
/// </para>
/// </summary>
public sealed class StronglyTypedIdPersistenceTests
{
    public static TheoryData<string> Engines() => ["Sqlite", "SqlServer", "Postgres"];

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryEngine_MapsAWrappedKeyToItsPrimitiveColumn(string engine)
    {
        using var context = CreateContext(engine);

        var key = context.Model.FindEntityType(typeof(WrappedOrder))!.FindProperty(nameof(WrappedOrder.Id))!;

        key.GetValueConverter()!.ProviderClrType.Should().Be<int>();
        key.GetValueConverter().Should().BeOfType<StronglyTypedIdValueConverter<OrderId, int>>();
        key.GetValueComparer().Should().BeOfType<StronglyTypedIdValueComparer<OrderId>>();
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryEngine_KeepsValueGenerationOnAWrappedKey(string engine)
    {
        using var context = CreateContext(engine);

        // The reason the registration is pre-convention rather than a model-finalizing convention: a
        // converter attached after the provider's value-generation conventions have run leaves the
        // wrapped key with no store-generated strategy at all.
        context.Model.FindEntityType(typeof(WrappedOrder))!
            .FindProperty(nameof(WrappedOrder.Id))!.ValueGenerated
            .Should().Be(ValueGenerated.OnAdd);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryEngine_ConvertsANonKeyWrappedProperty(string engine)
    {
        using var context = CreateContext(engine);

        // The cross-module scalar reference: the shape ADR-085 named as where the transposition risk
        // actually lives, so it is the one the mapping must not miss.
        context.Model.FindEntityType(typeof(WrappedOrder))!
            .FindProperty(nameof(WrappedOrder.CustomerId))!
            .GetValueConverter()!.ProviderClrType.Should().Be<int>();

        // And the nullable form, which the registration covers without a separate Nullable<> entry.
        context.Model.FindEntityType(typeof(WrappedSpeaker))!
            .FindProperty(nameof(WrappedSpeaker.SessionId))!
            .GetValueConverter()!.ProviderClrType.Should().Be<long>();
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryEngine_MapsAWrappedGuidKeyToAGuidColumn(string engine)
    {
        using var context = CreateContext(engine);

        context.Model.FindEntityType(typeof(WrappedSpeaker))!
            .FindProperty(nameof(WrappedSpeaker.Id))!
            .GetValueConverter()!.ProviderClrType.Should().Be<Guid>();
    }

    [Fact]
    public void WithoutTheRegistry_NothingIsConverted()
    {
        // Opting in is what turns the mapping on. Without the registry the model reaches EF's own
        // "the database provider does not support this type" validation, which is exactly the state
        // a host that never calls AddStronglyTypedIds is in, and exactly why the capability is
        // additive rather than a migration: such a host declares no wrappers for it to reach.
        using var context = WrappedIdBareSqliteContext.Create();

        var act = () => context.Model.FindEntityType(typeof(WrappedOrder));

        act.Should().Throw<InvalidOperationException>().WithMessage("*OrderId*value converter*");
    }

    [Fact]
    public async Task SqliteRoundTrip_GeneratesAWrappedIntKeyAndReadsItBack()
    {
        await using var context = WrappedIdSqliteContext.Create();

        var order = new WrappedOrder { Id = default, Code = "ORD-1", CustomerId = CustomerId.From(11) };
        context.Orders.Add(order);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        order.Id.Value.Should().BeGreaterThan(0, "SQLite stamped the identity key through the converter");

        context.ChangeTracker.Clear();
        var reloaded = await context.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken);

        reloaded.Id.Should().Be(order.Id);
        reloaded.CustomerId.Should().Be(CustomerId.From(11));
        reloaded.Code.Should().Be("ORD-1");
    }

    [Fact]
    public async Task SqliteRoundTrip_PersistsAWrappedGuidKeyAndANullableWrapper()
    {
        await using var context = WrappedIdSqliteContext.Create();

        var speakerId = SpeakerId.From(Guid.NewGuid());
        context.Speakers.Add(new WrappedSpeaker { Id = speakerId, DisplayName = "Ada", SessionId = null });
        context.Speakers.Add(new WrappedSpeaker
        {
            Id = SpeakerId.From(Guid.NewGuid()),
            DisplayName = "Grace",
            SessionId = LineId.From(9),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();

        var ada = await context.Speakers.SingleAsync(s => s.Id == speakerId, TestContext.Current.CancellationToken);
        ada.SessionId.Should().BeNull("a null optional identifier stays NULL rather than collapsing onto the default");

        var grace = await context.Speakers.SingleAsync(
            s => s.DisplayName == "Grace", TestContext.Current.CancellationToken);
        grace.SessionId.Should().Be(LineId.From(9));
    }

    [Fact]
    public async Task SqliteRoundTrip_StoresThePrimitiveInTheColumn()
    {
        await using var context = WrappedIdSqliteContext.Create();

        context.Speakers.Add(new WrappedSpeaker
        {
            Id = SpeakerId.From(Guid.NewGuid()),
            DisplayName = "Ada",
            SessionId = LineId.From(77),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Read the raw column, bypassing the converter: the schema a wrapper produces has to be the
        // schema the primitive produced, or adopting one would be a migration.
        var raw = await context.Database
            .SqlQueryRaw<long>("SELECT SessionId AS Value FROM WrappedSpeakers")
            .ToListAsync(TestContext.Current.CancellationToken);

        raw.Should().ContainSingle().Which.Should().Be(77L);
    }

    [Fact]
    public async Task FilterAndSort_TranslateToSqlAgainstAWrappedColumn()
    {
        await using var context = WrappedIdSqliteContext.Create();

        context.Orders.Add(new WrappedOrder { Id = default, Code = "A", CustomerId = CustomerId.From(2) });
        context.Orders.Add(new WrappedOrder { Id = default, Code = "B", CustomerId = CustomerId.From(1) });
        context.Orders.Add(new WrappedOrder { Id = default, Code = "C", CustomerId = CustomerId.From(1) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        // The client still sends the primitive; the strategy wraps it and EF's converter unwraps it
        // again into the parameter, so the predicate reaches the database as an int comparison.
        var filtered = QueryFilterService.ApplyFilters(
            context.Orders.AsQueryable(),
            new Dictionary<string, (string Operator, string Value)>(StringComparer.Ordinal)
            {
                ["CustomerId"] = ("equals", "1"),
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var matches = await filtered
            .OrderByDescending(o => o.Code)
            .ToListAsync(TestContext.Current.CancellationToken);

        matches.Should().HaveCount(2);
        matches[0].Code.Should().Be("C");

        // Sorting by a wrapped key is an ORDER BY over the converted column (ADR-034 sort keys).
        var sorted = await context.Orders
            .OrderByDescending(o => o.Id)
            .Select(o => o.Code)
            .ToListAsync(TestContext.Current.CancellationToken);

        sorted.Should().Equal("C", "B", "A");
    }

    [Fact]
    public async Task InFilter_TranslatesToSqlAgainstAWrappedColumn()
    {
        await using var context = WrappedIdSqliteContext.Create();

        context.Orders.Add(new WrappedOrder { Id = default, Code = "A", CustomerId = CustomerId.From(5) });
        context.Orders.Add(new WrappedOrder { Id = default, Code = "B", CustomerId = CustomerId.From(6) });
        context.Orders.Add(new WrappedOrder { Id = default, Code = "C", CustomerId = CustomerId.From(7) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var filtered = QueryFilterService.ApplyFilters(
            context.Orders.AsQueryable(),
            new Dictionary<string, (string Operator, string Value)>(StringComparer.Ordinal)
            {
                ["CustomerId"] = ("in", "5,7"),
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var codes = await filtered
            .OrderBy(o => o.Code)
            .Select(o => o.Code)
            .ToListAsync(TestContext.Current.CancellationToken);

        codes.Should().Equal("A", "C");
    }

    [Fact]
    public void Comparer_UsesStructuralEquality()
    {
        var comparer = new StronglyTypedIdValueComparer<OrderId>();

        comparer.Equals(OrderId.From(5), OrderId.From(5)).Should().BeTrue();
        comparer.Equals(OrderId.From(5), OrderId.From(6)).Should().BeFalse();
        comparer.GetHashCode(OrderId.From(5)).Should().Be(comparer.GetHashCode(OrderId.From(5)));
        comparer.Snapshot(OrderId.From(5)).Should().Be(OrderId.From(5));
    }

    [Fact]
    public void Converter_RoundTripsBothLegs()
    {
        var converter = new StronglyTypedIdValueConverter<OrderId, int>();

        converter.ConvertToProvider(OrderId.From(5)).Should().Be(5);
        converter.ConvertFromProvider(5).Should().Be(OrderId.From(5));
    }

    [Fact]
    public void NullableConverter_PassesNullThroughBothLegs()
    {
        var converter = new NullableStronglyTypedIdValueConverter<OrderId, int>();

        converter.ConvertToProvider(null).Should().BeNull();
        converter.ConvertFromProvider(null).Should().BeNull();
        converter.ConvertToProvider(OrderId.From(5)).Should().Be(5);
        converter.ConvertFromProvider(5).Should().Be(OrderId.From(5));
    }

    private static ApplicationDbContext CreateContext(string engine) => engine switch
    {
        "Sqlite" => WrappedIdSqliteContext.Create(),
        "SqlServer" => WrappedIdSqlServerContext.Create(),
        "Postgres" => WrappedIdPostgresContext.Create(),
        _ => throw new NotSupportedException(engine),
    };
}
