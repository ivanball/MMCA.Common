using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Parameterized-SQL-only ban, driven by the shared source-scan base
/// (<see cref="RawSqlConventionTestsBase"/>). MMCA.Common declares no business modules, so the
/// framework's own Application and Infrastructure projects are scanned instead: the framework is the
/// one place that ships a raw-SQL surface for consumers to use, so it is the one place that must not
/// leave a concatenated statement lying around as the example.
/// </summary>
public sealed class RawSqlConventionTests : RawSqlConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <inheritdoc />
    protected override IEnumerable<string> ScannedSourceDirectories()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx");
        yield return Path.Combine(repoRoot, "Source", "Core", "MMCA.Common.Application");
        yield return Path.Combine(repoRoot, "Source", "Core", "MMCA.Common.Infrastructure");
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> AllowedFiles =>
    [
        // SET IDENTITY_INSERT [schema].[table] ON/OFF. A T-SQL identifier cannot be a command
        // parameter, so this statement has no parameterized form; the two names are read off EF model
        // metadata (entityType.GetSchema()/GetTableName()), never off caller input, and the call site
        // carries the matching S2077 suppression with the same reasoning.
        "DbContextFactory.cs",
    ];
}
