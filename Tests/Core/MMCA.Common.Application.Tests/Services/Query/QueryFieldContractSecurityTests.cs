using AwesomeAssertions;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Application.Services.Query;

namespace MMCA.Common.Application.Tests.Services.Query;

/// <summary>
/// Security tests for the query-surface hardening: a client-supplied sort column, filter key or
/// lookup name must resolve against the RESPONSE contract, never by reflecting over the entity or
/// walking a navigation path the client spelled
/// (SEC-Common-24, SEC-Common-25, SEC-ADC-09, SEC-Store-13).
/// </summary>
public sealed class QueryFieldContractSecurityTests
{
    public sealed class Speaker
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Redacted per role by the DTO mapper; still a real entity column.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>Audit column no DTO carries.</summary>
        public string CreatedBy { get; set; } = string.Empty;

        public Speaker? Mentor { get; set; }
    }

    public sealed class SpeakerDto
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private static readonly Dictionary<string, string> NoMap = new(StringComparer.OrdinalIgnoreCase);

    private static IQueryable<Speaker> Rows() => new List<Speaker>
    {
        new() { Id = 1, Name = "b", Email = "z@example.com", CreatedBy = "sys" },
        new() { Id = 2, Name = "a", Email = "a@example.com", CreatedBy = "ops" },
    }.AsQueryable();

    private static QueryFieldContract Contract => QueryFieldContract.For<SpeakerDto>();

    // ── SEC-ADC-09: sorting resolves against the DTO, not the entity ──
    [Fact]
    public void ApplySorting_WithContract_IgnoresEntityOnlyColumn()
    {
        var sorted = QueryFieldService.ApplySorting(
            Rows(),
            sortColumn: "Email",
            sortDirection: "asc",
            NoMap,
            Contract);

        // Falling back means the input order survives; ordering by Email would put row 2 first.
        sorted.Select(s => s.Id).Should().Equal(1, 2);
    }

    [Fact]
    public void ApplySorting_WithContract_StillSortsByContractColumn()
    {
        var sorted = QueryFieldService.ApplySorting(
            Rows(),
            sortColumn: "Name",
            sortDirection: "asc",
            NoMap,
            Contract);

        sorted.Select(s => s.Id).Should().Equal(2, 1);
    }

    [Fact]
    public void ApplySorting_WithoutContract_KeepsHistoricalEntityBehaviour()
    {
        var sorted = QueryFieldService.ApplySorting(
            Rows(),
            sortColumn: "Email",
            sortDirection: "asc",
            NoMap);

        sorted.Select(s => s.Id).Should().Equal(2, 1);
    }

    [Fact]
    public void ApplySorting_WithContract_HonoursServerAuthoredMapEntry()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MentorName"] = "Mentor.Name"
        };

        var resolved = QueryFieldService.Validate<Speaker>("MentorName", map, allowWriteableFields: true, Contract);

        resolved.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithContract_RejectsAuditColumn()
    {
        var result = QueryFieldService.Validate<Speaker>("CreatedBy", NoMap, allowWriteableFields: true, Contract);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("response contract", StringComparison.Ordinal));
    }

    // ── SEC-Common-25: filter keys resolve against the DTO, dotted client keys are refused ──
    [Fact]
    public void ValidateFilters_WithContract_RejectsEntityOnlyColumn()
    {
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Email"] = ("STARTS WITH", "a")
        };

        var result = QueryFilterService.ValidateFilters<Speaker>(filters, NoMap, Contract);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_WithContract_RejectsClientAuthoredNavigationPath()
    {
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mentor.Email"] = ("STARTS WITH", "a")
        };

        var result = QueryFilterService.ValidateFilters<Speaker>(filters, NoMap, Contract);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ApplyFilters_WithContract_DropsEntityOnlyColumnInsteadOfFiltering()
    {
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Email"] = ("STARTS WITH", "a")
        };

        var filtered = QueryFilterService.ApplyFilters(Rows(), filters, NoMap, Contract);

        // The oracle the finding describes works by row-count differences: with the contract on,
        // the predicate is never built, so the count cannot leak anything.
        filtered.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyFilters_WithoutContract_StillFiltersOnEntityColumn()
    {
        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Email"] = ("STARTS WITH", "a")
        };

        var filtered = QueryFilterService.ApplyFilters(Rows(), filters, NoMap);

        filtered.Should().HaveCount(1);
    }

    // ── SEC-Store-13: navigation depth ceiling, whoever authored the path ──
    [Fact]
    public void ValidateFilters_RejectsPathDeeperThanCeiling_EvenWithoutContract()
    {
        var deep = string.Join('.', Enumerable.Repeat("Mentor", QueryFieldContract.MaxNavigationDepth)) + ".Name";

        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            [deep] = ("STARTS WITH", "a")
        };

        var result = QueryFilterService.ValidateFilters<Speaker>(filters, NoMap);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ApplyFilters_DropsPathDeeperThanCeiling_EvenWithoutContract()
    {
        var deep = string.Join('.', Enumerable.Repeat("Mentor", 200)) + ".Name";

        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            [deep] = ("STARTS WITH", "a")
        };

        var filtered = QueryFilterService.ApplyFilters(Rows(), filters, NoMap);

        // No join chain is built at all, so the query is the untouched source.
        filtered.Should().HaveCount(2);
    }

    [Fact]
    public void ValidateFilters_RejectsServerAuthoredMapEntryDeeperThanCeiling()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chain"] = "Mentor.Mentor.Mentor.Name"
        };

        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chain"] = ("STARTS WITH", "a")
        };

        var result = QueryFilterService.ValidateFilters<Speaker>(filters, map, Contract);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ValidateFilters_AllowsMapEntryAtTheCeiling()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MentorMentorName"] = "Mentor.Mentor.Name"
        };

        var filters = new Dictionary<string, (string Operator, string Value)>(StringComparer.OrdinalIgnoreCase)
        {
            ["MentorMentorName"] = ("STARTS WITH", "a")
        };

        var result = QueryFilterService.ValidateFilters<Speaker>(filters, map, Contract);

        result.IsSuccess.Should().BeTrue();
    }

    // ── The contract itself ──
    [Fact]
    public void Contract_AdmitsDtoNamesCaseInsensitivelyAndNothingElse()
    {
        var contract = QueryFieldContract.For<SpeakerDto>();

        contract.AllowsClientKey("name").Should().BeTrue();
        contract.AllowsClientKey("Email").Should().BeFalse();
        contract.AllowsClientKey("Name.Something").Should().BeFalse();
        contract.Contains("ID").Should().BeTrue();
        contract.Names.Should().HaveCount(2);
    }

    [Fact]
    public void ForNames_NarrowsBelowTheDtoDeclaration()
    {
        var contract = QueryFieldContract.ForNames(["Name"]);

        contract.AllowsClientKey("Name").Should().BeTrue();
        contract.AllowsClientKey("Id").Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Name", true)]
    [InlineData("Category.Name", true)]
    [InlineData("A.B.C", true)]
    [InlineData("A.B.C.D", false)]
    public void IsWithinNavigationDepth_CountsSegments(string? path, bool expected)
        => QueryFieldContract.IsWithinNavigationDepth(path).Should().Be(expected);
}
