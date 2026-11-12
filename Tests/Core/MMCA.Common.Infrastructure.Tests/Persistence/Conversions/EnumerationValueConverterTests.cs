using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conversions;

/// <summary>
/// Tests for <see cref="EnumerationValueConverter{TEnumeration}"/> and
/// <see cref="NullableEnumerationValueConverter{TEnumeration}"/>: the packaged replacement for the
/// <c>m =&gt; m.Value, v =&gt; Enumeration&lt;T&gt;.FromValue(v).Value!</c> lambda pair an entity
/// configuration would otherwise hand-roll for every smart enumeration.
/// </summary>
public sealed class EnumerationValueConverterTests
{
    // ── Non-nullable converter ──
    [Fact]
    public void Converter_RoundTrips_MemberThroughItsIntegerValue()
    {
        var converter = new EnumerationValueConverter<Priority>();

        int stored = converter.ConvertToProviderExpression.Compile()(Priority.High);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be(3, "the column stores the member's stable integer value");
        read.Should().BeSameAs(Priority.High, "the read leg resolves the interned singleton");
    }

    [Fact]
    public void Converter_MapsToAndFromInt_SoTheColumnStaysAPlainIntColumn()
    {
        var converter = new EnumerationValueConverter<Priority>();

        converter.ModelClrType.Should().Be<Priority>();
        converter.ProviderClrType.Should().Be<int>();
    }

    [Fact]
    public void Converter_ReadLeg_ResolvesEveryDeclaredMember()
    {
        var read = new EnumerationValueConverter<Priority>().ConvertFromProviderExpression.Compile();

        read(1).Should().BeSameAs(Priority.Low);
        read(2).Should().BeSameAs(Priority.Normal);
        read(3).Should().BeSameAs(Priority.High);
    }

    [Fact]
    public void Converter_WriteLeg_MatchesTheHandRolledLambdaItReplaces()
    {
        var converter = new EnumerationValueConverter<Priority>();

        converter.ConvertToProviderExpression.Compile()(Priority.Low).Should().Be(HandRolled(Priority.Low));

        static int HandRolled(Priority p) => p.Value;
    }

    // ── Nullable converter ──
    [Fact]
    public void NullableConverter_RoundTripsAValue()
    {
        var converter = new NullableEnumerationValueConverter<Priority>();

        int? stored = converter.ConvertToProviderExpression.Compile()(Priority.Low);
        var read = converter.ConvertFromProviderExpression.Compile()(stored);

        stored.Should().Be(1);
        read.Should().BeSameAs(Priority.Low);
    }

    [Fact]
    public void NullableConverter_PassesNullThroughBothLegs()
    {
        var converter = new NullableEnumerationValueConverter<Priority>();

        converter.ConvertToProviderExpression.Compile()(null).Should().BeNull();
        converter.ConvertFromProviderExpression.Compile()(null).Should().BeNull(
            "an absent member must stay a NULL column value, not collapse onto the member valued zero");
    }

    [Fact]
    public void NullableConverter_MapsToAndFromNullableInt()
    {
        var converter = new NullableEnumerationValueConverter<Priority>();

        converter.ModelClrType.Should().Be<Priority>();
        converter.ProviderClrType.Should().Be<int?>();
    }

    // -- Name converter --
    // For a column that already stores the member name as text: adopting a smart enumeration over
    // an existing string column must not turn into a column type change with a data rewrite.
    [Fact]
    public void NameConverter_WritesName()
    {
        var converter = new EnumerationNameValueConverter<Priority>();

        converter.ConvertToProviderExpression.Compile()(Priority.High).Should().Be(
            "High",
            "the column stores the member's canonical name");
    }

    [Fact]
    public void NameConverter_ReadsNameCaseInsensitively()
    {
        var read = new EnumerationNameValueConverter<Priority>().ConvertFromProviderExpression.Compile();

        read("Normal").Should().BeSameAs(Priority.Normal, "the read leg resolves the interned singleton");
        read("normal").Should().BeSameAs(Priority.Normal, "FromName matches case-insensitively");
        read("HIGH").Should().BeSameAs(Priority.High);
    }

    // The int converter's read-leg contract, carried over: the read leg trusts the column, so a name
    // no member declares makes FromName fail and the null-forgiving .Value! yields null for the row.
    [Fact]
    public void NameConverter_UnknownName_MaterializesNull()
    {
        var read = new EnumerationNameValueConverter<Priority>().ConvertFromProviderExpression.Compile();

        read("Urgent").Should().BeNull("an unknown name follows the same contract as an unknown int value");
    }

    // There is no nullable name converter, so null only reaches the read leg as text no member
    // declares; it takes the same path as an unknown name rather than throwing.
    [Fact]
    public void NameConverter_NullText_MaterializesNull()
    {
        var read = new EnumerationNameValueConverter<Priority>().ConvertFromProviderExpression.Compile();

        read(null!).Should().BeNull("FromName treats null as the empty name, which no member declares");
    }

    [Fact]
    public void NameConverter_RoundTripsEveryMember()
    {
        var converter = new EnumerationNameValueConverter<Priority>();
        var write = converter.ConvertToProviderExpression.Compile();
        var read = converter.ConvertFromProviderExpression.Compile();

        Enumeration<Priority>.All.Should().HaveCount(3);
        foreach (var member in Enumeration<Priority>.All)
        {
            string stored = write(member);

            stored.Should().Be(member.Name);
            read(stored).Should().BeSameAs(member);
        }
    }

    [Fact]
    public void NameConverter_MapsToAndFromString_SoTheColumnStaysATextColumn()
    {
        var converter = new EnumerationNameValueConverter<Priority>();

        converter.ModelClrType.Should().Be<Priority>();
        converter.ProviderClrType.Should().Be<string>();
    }

    [Fact]
    public async Task NameConverter_SqliteRoundTrip_StoresTheNameAndReadsTheMemberBack()
    {
        await using var context = PriorityRowContext.Create();

        context.Rows.Add(new PriorityRow { Id = 1, Priority = Priority.High });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        // Read the raw column, bypassing the converter: the stored text is the member name.
        var raw = await context.Database
            .SqlQueryRaw<string>("SELECT Priority AS Value FROM PriorityRows")
            .ToListAsync(TestContext.Current.CancellationToken);
        raw.Should().ContainSingle().Which.Should().Be("High");

        var reloaded = await context.Rows.SingleAsync(r => r.Id == 1, TestContext.Current.CancellationToken);
        reloaded.Priority.Should().BeSameAs(Priority.High);
    }

    private sealed class PriorityRow
    {
        public int Id { get; set; }

        public Priority Priority { get; set; } = null!;
    }

    /// <summary>A plain SQLite in-memory context holding one name-converted enumeration column.</summary>
    private sealed class PriorityRowContext(DbContextOptions<PriorityRowContext> options) : DbContext(options)
    {
        public DbSet<PriorityRow> Rows => Set<PriorityRow>();

        public static PriorityRowContext Create()
        {
            var options = new DbContextOptionsBuilder<PriorityRowContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new PriorityRowContext(options);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PriorityRow>(entity =>
            {
                entity.ToTable("PriorityRows");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();
                entity.Property(e => e.Priority)
                    .HasConversion(new EnumerationNameValueConverter<Priority>())
                    .HasMaxLength(32)
                    .IsRequired();
            });
    }

    private sealed class Priority : Enumeration<Priority>
    {
        public static readonly Priority Low = new(1, "Low");
        public static readonly Priority Normal = new(2, "Normal");
        public static readonly Priority High = new(3, "High");

        private Priority(int value, string name)
            : base(value, name)
        {
        }
    }
}
