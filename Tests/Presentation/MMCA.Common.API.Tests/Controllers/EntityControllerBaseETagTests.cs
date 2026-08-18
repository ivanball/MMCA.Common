using System.Dynamic;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// A single read emits the resource's concurrency token as a weak <c>ETag</c>, so a client can turn
/// straight around and send it as an <c>If-Match</c> precondition. A DTO with no token gets no header.
/// </summary>
public sealed class EntityControllerBaseETagTests
{
    private static readonly byte[] RowVersion = [0, 0, 0, 0, 0, 0, 7, 209];

    private static TController CreateController<TController>(Func<DefaultHttpContext, TController> factory)
        where TController : ControllerBase
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        return factory(httpContext);
    }

    private static VersionedEntityController CreateVersionedController(
        Mock<IEntityQueryService<VersionedEntity, VersionedDTO, int>> queryService) =>
        CreateController(httpContext => new VersionedEntityController(
            queryService.Object,
            new Mock<ILogger<EntityControllerBase<VersionedEntity, VersionedDTO, int>>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        });

    private static void SetupGetById(
        Mock<IEntityQueryService<VersionedEntity, VersionedDTO, int>> queryService,
        object? value) =>
        queryService
            .Setup(q => q.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<VersionedEntity, int>?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(value!));

    [Fact]
    public async Task GetByIdAsync_WhenTheDtoCarriesARowVersion_EmitsTheWeakETag()
    {
        var queryService = new Mock<IEntityQueryService<VersionedEntity, VersionedDTO, int>>();
        SetupGetById(queryService, new VersionedDTO { Id = 1, RowVersion = RowVersion });
        var sut = CreateVersionedController(queryService);

        await sut.GetByIdAsync(1, cancellationToken: CancellationToken.None);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be("W/\"AAAAAAAAB9E=\"");
    }

    [Fact]
    public async Task GetByIdAsync_WhenTheRowVersionIsEmpty_EmitsNoETag()
    {
        var queryService = new Mock<IEntityQueryService<VersionedEntity, VersionedDTO, int>>();
        SetupGetById(queryService, new VersionedDTO { Id = 1, RowVersion = [] });
        var sut = CreateVersionedController(queryService);

        await sut.GetByIdAsync(1, cancellationToken: CancellationToken.None);

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public async Task GetByIdAsync_WhenTheReadFails_EmitsNoETag()
    {
        var queryService = new Mock<IEntityQueryService<VersionedEntity, VersionedDTO, int>>();
        queryService
            .Setup(q => q.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<VersionedEntity, int>?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<object>(Error.NotFoundError("Test.NotFound", "Nope")));
        var sut = CreateVersionedController(queryService);

        await sut.GetByIdAsync(1, cancellationToken: CancellationToken.None);

        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public async Task GetByIdAsync_WithAFieldProjectionThatKeptTheToken_StillEmitsTheETag()
    {
        var shaped = new ExpandoObject();
        ((IDictionary<string, object?>)shaped)["id"] = 1;
        ((IDictionary<string, object?>)shaped)["rowVersion"] = RowVersion;

        var queryService = new Mock<IEntityQueryService<VersionedEntity, VersionedDTO, int>>();
        SetupGetById(queryService, shaped);
        var sut = CreateVersionedController(queryService);

        await sut.GetByIdAsync(1, fields: "id,rowVersion", cancellationToken: CancellationToken.None);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be("W/\"AAAAAAAAB9E=\"");
    }

    [Fact]
    public async Task GetByIdAsync_WhenTheDtoHasNoRowVersion_EmitsNoETag()
    {
        var queryService = new Mock<IEntityQueryService<PlainEntity, PlainDTO, int>>();
        queryService
            .Setup(q => q.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<Specification<PlainEntity, int>?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<object>(new PlainDTO { Id = 1 }));

        var sut = CreateController(httpContext => new PlainEntityController(
            queryService.Object,
            new Mock<ILogger<EntityControllerBase<PlainEntity, PlainDTO, int>>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        });

        await sut.GetByIdAsync(1, cancellationToken: CancellationToken.None);

        sut.Response.Headers.Should().NotContainKey(
            ConcurrencyETag.ETagHeaderName,
            "a resource with no version has no precondition to offer");
    }
}

/// <summary>An entity whose DTO round-trips a concurrency token.</summary>
public sealed class VersionedEntity : AuditableBaseEntity<int>;

/// <summary>A DTO that carries the token, the shape an ETag is built from.</summary>
public sealed record VersionedDTO : IBaseDTO<int>, IConcurrencyAware
{
    /// <inheritdoc />
    public required int Id { get; init; }

    /// <inheritdoc />
    public byte[]? RowVersion { get; init; }
}

/// <summary>An entity whose DTO carries no token at all.</summary>
public sealed class PlainEntity : AuditableBaseEntity<int>;

/// <summary>A DTO with no concurrency token.</summary>
public sealed record PlainDTO : IBaseDTO<int>
{
    /// <inheritdoc />
    public required int Id { get; init; }
}

/// <summary>Concrete controller over <see cref="VersionedDTO"/>.</summary>
public sealed class VersionedEntityController(
    IEntityQueryService<VersionedEntity, VersionedDTO, int> queryService,
    ILogger<EntityControllerBase<VersionedEntity, VersionedDTO, int>> logger)
    : EntityControllerBase<VersionedEntity, VersionedDTO, int>(queryService, logger);

/// <summary>Concrete controller over <see cref="PlainDTO"/>.</summary>
public sealed class PlainEntityController(
    IEntityQueryService<PlainEntity, PlainDTO, int> queryService,
    ILogger<EntityControllerBase<PlainEntity, PlainDTO, int>> logger)
    : EntityControllerBase<PlainEntity, PlainDTO, int>(queryService, logger);
