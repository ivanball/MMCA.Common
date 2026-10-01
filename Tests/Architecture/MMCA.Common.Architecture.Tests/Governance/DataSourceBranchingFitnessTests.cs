using System.Globalization;
using System.Text.RegularExpressions;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Engine-specific behavior lives behind <c>IDataSourceEngine</c> (ADR-130): one strategy per engine,
/// resolved by the <c>DataSource</c> value, so a fifth engine or a changed engine rule is one class and
/// not a hunt through every <see langword="switch"/>. Nothing in the compiler stops a new
/// <c>if (engine == DataSource.Sqlite)</c> or <c>context is SqliteDbContext</c> branch from growing back
/// outside the strategies, so this gate scans <c>Source/**/*.cs</c> as text and fails on any naming of a
/// concrete engine value or engine context type outside the allow-list below. The allow-list is exact at
/// file granularity: a listed file that no longer names an engine fails too, so the list shrinks with
/// the code instead of quietly keeping a pass for a file that could regress later.
/// </summary>
public sealed partial class DataSourceBranchingFitnessTests
{
    private const string Remedy = "route engine-specific behavior through IDataSourceEngine (ADR-130)";

    /// <summary>
    /// Files under <c>Source/</c> (forward slashes) that may name a concrete engine, each with its reason.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.Ordinal)
    {
        // The engine strategies themselves: each declares the one DataSource value it implements.
        ["Core/MMCA.Common.Infrastructure/Persistence/DataSources/Engines/CosmosDataSourceEngine.cs"] = "engine strategy",
        ["Core/MMCA.Common.Infrastructure/Persistence/DataSources/Engines/PostgreSQLDataSourceEngine.cs"] = "engine strategy",
        ["Core/MMCA.Common.Infrastructure/Persistence/DataSources/Engines/SQLServerDataSourceEngine.cs"] = "engine strategy",
        ["Core/MMCA.Common.Infrastructure/Persistence/DataSources/Engines/SqliteDataSourceEngine.cs"] = "engine strategy",

        // The four sealed contexts (one class per engine, ADR-006): each applies the configurations of
        // its own engine and names nothing else.
        ["Core/MMCA.Common.Infrastructure/Persistence/DbContexts/CosmosDbContext.cs"] = "sealed context naming its own engine",
        ["Core/MMCA.Common.Infrastructure/Persistence/DbContexts/PostgreSQLDbContext.cs"] = "sealed context naming its own engine",
        ["Core/MMCA.Common.Infrastructure/Persistence/DbContexts/SQLServerDbContext.cs"] = "sealed context naming its own engine",
        ["Core/MMCA.Common.Infrastructure/Persistence/DbContexts/SqliteDbContext.cs"] = "sealed context naming its own engine",

        // The configuration shim bases: [UseDataSource(DataSource.X)] is how an entity picks its engine.
        ["Core/MMCA.Common.Infrastructure/Persistence/Configuration/EntityTypeConfiguration/EntityTypeConfigurationCosmos.cs"] = "configuration shim base",
        ["Core/MMCA.Common.Infrastructure/Persistence/Configuration/EntityTypeConfiguration/EntityTypeConfigurationPostgreSQL.cs"] = "configuration shim base",
        ["Core/MMCA.Common.Infrastructure/Persistence/Configuration/EntityTypeConfiguration/EntityTypeConfigurationSQLServer.cs"] = "configuration shim base",
        ["Core/MMCA.Common.Infrastructure/Persistence/Configuration/EntityTypeConfiguration/EntityTypeConfigurationSqlite.cs"] = "configuration shim base",

        // Settings defaults: the framework tables default to the framework-default engine; a value, not a branch.
        ["Core/MMCA.Common.Infrastructure/Scheduling/SchedulerSettings.cs"] = "settings default engine",
        ["Core/MMCA.Common.Infrastructure/Persistence/AuditTrail/AuditTrailSettings.cs"] = "settings default engine",
        ["Core/MMCA.Common.Infrastructure/Persistence/Outbox/Administration/OutboxSettings.cs"] = "settings default engine",
        ["Core/MMCA.Common.Infrastructure/Persistence/InternalCommands/Administration/InternalCommandsSettings.cs"] = "settings default engine",

        // Design time: CreateSqlServer / CreatePostgreSql / CreateSqlite each build their own engine's context.
        ["Core/MMCA.Common.Infrastructure/Persistence/DbContexts/Design/DesignTimeDbContextHelper.cs"] = "per-engine design-time entry points",

        // The engine parameter defaults to SQL Server to match EntityTypeConfigurationSQLServer; the
        // engine-specific filter text itself comes from the engine strategy.
        ["Core/MMCA.Common.Infrastructure/Persistence/Configuration/IndexBuilderExtensions.cs"] = "default engine parameter and its doc",

        // Owns the framework-default engine constant and documents it.
        ["Core/MMCA.Common.Infrastructure/Persistence/DataSources/DataSourceResolver.cs"] = "framework-default engine constant and doc",

        // Framework-default-engine requests: ResolveLogical(DataSource.SQLServer, Default) asks for the
        // framework tables' source; the resolver maps it onto whatever the host configured.
        ["Core/MMCA.Common.Infrastructure/Persistence/Auth/RefreshSessionCleanupService.cs"] = "framework-default engine request",
        ["Core/MMCA.Common.Infrastructure/Persistence/Auth/EFRefreshSessionStore.cs"] = "framework-default engine request",
        ["Core/MMCA.Common.Infrastructure/Persistence/Auth/EFPermissionGrantStore.cs"] = "framework-default engine request",
        ["Core/MMCA.Common.Infrastructure/Persistence/EFRawSqlQueryExecutor.cs"] = "framework-default engine request",
        ["Core/MMCA.Common.Infrastructure/Persistence/DbContexts/Factory/DbContextFactory.cs"] = "framework-default engine request",
    };

    [Fact]
    public void ConcreteEngineBranching_StaysInsideTheEngineStrategies()
    {
        var hits = ScanSource();

        var violations = hits
            .Where(hit => !AllowedFiles.ContainsKey(hit.File))
            .Select(hit => $"  - Source/{hit.File}:{hit.Line.ToString(CultureInfo.InvariantCulture)}: {hit.Text}");

        ArchitectureAssert.NoViolations(violations, $"a concrete engine is named outside the allow-list; {Remedy}");
    }

    [Fact]
    public void AllowList_HasNoStaleEntries()
    {
        var filesWithHits = ScanSource().Select(hit => hit.File).ToHashSet(StringComparer.Ordinal);

        var stale = AllowedFiles.Keys
            .Where(file => !filesWithHits.Contains(file))
            .Select(file => $"  - Source/{file}");

        ArchitectureAssert.NoViolations(
            stale,
            "an allow-listed file no longer names a concrete engine; remove its entry so a later regression there is caught");
    }

    [Theory]
    [InlineData("if (engine == DataSource.Sqlite)")]
    [InlineData("case DataSource.CosmosDB:")]
    [InlineData("if (context is SqliteDbContext)")]
    [InlineData("if (context is not CosmosDbContext cosmos)")]
    [InlineData("return db is NpgsqlDbContext;")]
    public void Detector_Flags_ConcreteEngineBranching(string line) =>
        EngineReference.IsMatch(line).Should().BeTrue("the line names a concrete engine");

    [Theory]
    [InlineData("var engine = dataSourceEngines.Resolve(key.Engine);")]
    [InlineData("public DataSource DataSource { get; init; }")]
    [InlineData("if (context is ApplicationDbContext)")]
    public void Detector_Accepts_EngineNeutralCode(string line) =>
        EngineReference.IsMatch(line).Should().BeFalse("the line names no concrete engine");

    private static List<EngineHit> ScanSource()
    {
        var sourceRoot = Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"), "Source");
        var files = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Non-vacuous floor: a wrong scan root must not let the gate pass having read nothing.
        files.Should().HaveCountGreaterThan(500, "the scan must cover the framework's Source tree");

        var hits = new List<EngineHit>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (EngineReference.IsMatch(lines[i]))
                {
                    hits.Add(new EngineHit(relative, i + 1, lines[i].Trim()));
                }
            }
        }

        return hits;
    }

    [GeneratedRegex(
        @"DataSource\.(CosmosDB|Sqlite|SQLServer|PostgreSQL)|is (not )?\w*(SQLServer|Cosmos|Sqlite|PostgreSQL|Npgsql)\w*DbContext",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex EngineReference { get; }

    /// <summary>One line naming a concrete engine: the file under <c>Source/</c>, its 1-based line, and the text.</summary>
    private sealed record EngineHit(string File, int Line, string Text);
}
