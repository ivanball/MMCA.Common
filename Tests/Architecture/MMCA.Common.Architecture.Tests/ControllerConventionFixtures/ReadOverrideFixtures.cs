using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Architecture.Tests.ControllerConventionFixtures;

/// <summary>
/// Compiled read-override shapes for <c>ControllerReadOverrideRuleTests</c>: one controller per way of
/// satisfying or breaking the rule that a controller never overrides a base read action only to add
/// <c>[AllowAnonymous]</c> or <c>[OutputCache]</c> (that is <c>[PublicCachedReads]</c>'s job). None
/// declares a POST, and each takes two constructor dependencies, so the other fixture-assembly scans
/// (idempotency, constructor-dependency ceiling) are unaffected. The two <c>[AllowAnonymous]</c>
/// fixtures are allow-listed in <c>AnonymousEndpointTestsBaseTests</c>.
/// </summary>
public sealed class ControllerConventionFixtureEntity : AuditableBaseEntity<int>;

/// <summary>The DTO every controller-convention fixture serves.</summary>
public sealed record ControllerConventionFixtureDto : IBaseDTO<int>
{
    public required int Id { get; init; }
}

/// <summary>Violation: overrides <c>GetByIdAsync</c> only to add <c>[AllowAnonymous]</c>.</summary>
public sealed class AllowAnonymousReadOverrideFixtureController(
    IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
    ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
    : EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>(queryService, logger)
{
    [AllowAnonymous]
    public override Task<ActionResult<ControllerConventionFixtureDto>> GetByIdAsync(
        int id,
        bool includeFKs = true,
        bool includeChildren = false,
        [FromQuery] string? fields = null,
        CancellationToken cancellationToken = default) =>
        base.GetByIdAsync(id, includeFKs, includeChildren, fields, cancellationToken);
}

/// <summary>Violation: overrides the unpaged <c>GetAllAsync</c> only to add <c>[OutputCache]</c>.</summary>
public sealed class OutputCacheReadOverrideFixtureController(
    IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
    ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
    : EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>(queryService, logger)
{
    [OutputCache(PolicyName = "FixtureCache")]
    public override Task<ActionResult<CollectionResult<ControllerConventionFixtureDto>>> GetAllAsync(
        [FromQuery] string? fields = null,
        bool includeFKs = false,
        bool includeChildren = false,
        CancellationToken cancellationToken = default) =>
        base.GetAllAsync(fields, includeFKs, includeChildren, cancellationToken);
}

/// <summary>Compliant: overrides a base read with a real body and neither marker.</summary>
public sealed class BodyReadOverrideFixtureController(
    IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
    ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
    : EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>(queryService, logger)
{
    public override Task<ActionResult<CollectionResult<BaseLookup<int>>>> GetAllForLookupAsync(
        [Required] string nameProperty,
        CancellationToken cancellationToken = default)
    {
        var trimmed = nameProperty.Trim();
        return base.GetAllForLookupAsync(trimmed, cancellationToken);
    }
}

/// <summary>Compliant: an app-declared action may carry both markers; only base-read overrides are governed.</summary>
public sealed class AppActionMarkersFixtureController(
    IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
    ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
    : EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>(queryService, logger)
{
    [HttpGet("search")]
    [AllowAnonymous]
    [OutputCache(PolicyName = "FixtureCache")]
    public IActionResult Search() => Ok();
}
