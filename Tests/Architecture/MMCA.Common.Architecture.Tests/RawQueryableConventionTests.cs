using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Raw-IQueryable ban for Application-layer code, driven by the shared source-scan base
/// (<see cref="RawQueryableConventionTestsBase"/>). MMCA.Common declares no business modules, so
/// the framework's own Application project is scanned instead; the allowlist records the
/// framework-owned building blocks that legitimately sit on the EF-aware queryable surfaces
/// (the generic query pipeline and the Notifications inbox handlers, whose cross-entity joins are
/// the documented exception).
/// </summary>
public sealed class RawQueryableConventionTests : RawQueryableConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <inheritdoc />
    protected override IEnumerable<string> ApplicationSourceDirectories()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx");
        yield return Path.Combine(repoRoot, "Source", "Core", "MMCA.Common.Application");
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> AllowedFiles =>
    [
        // The generic entity-query pipeline is the framework's deliberate IQueryable composition root.
        "EntityQueryService.cs",

        // Notifications handlers: cross-entity joins/projections over the notification store are the
        // framework's documented raw-queryable exception (they never cross a module boundary).
        "GetNotificationHistoryHandler.cs",
        "GetMyNotificationsHandler.cs",
        "GetUnreadNotificationCountHandler.cs",
        "MarkNotificationReadHandler.cs",
        "MarkAllNotificationsReadHandler.cs",
    ];
}
