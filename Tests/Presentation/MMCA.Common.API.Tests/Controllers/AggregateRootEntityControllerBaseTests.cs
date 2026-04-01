using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

public sealed class AggregateRootEntityControllerBaseTests
{
    private readonly Mock<IEntityQueryService<TestAggregateEntity, TestAggDTO, int>> _queryServiceMock = new();
    private readonly Mock<ICommandHandler<TestCreateRequest, Result<TestAggDTO>>> _createHandlerMock = new();
    private readonly Mock<ICommandHandler<DeleteEntityCommand<TestAggregateEntity, int>, Result>> _deleteHandlerMock = new();
    private readonly Mock<ILogger<EntityControllerBase<TestAggregateEntity, TestAggDTO, int>>> _loggerMock = new();

    private TestAggregateRootController CreateController()
    {
        var services = new ServiceCollection();
        var settingsMock = new Mock<IApplicationSettings>();
        settingsMock.Setup(s => s.MaxPageSize).Returns(100);
        services.AddSingleton(settingsMock.Object);

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        return new TestAggregateRootController(
            _queryServiceMock.Object,
            _createHandlerMock.Object,
            _deleteHandlerMock.Object,
            _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    // ── CreateAsync ──
    [Fact]
    public async Task CreateAsync_Success_ReturnsCreatedAtRoute()
    {
        var request = new TestCreateRequest();
        var dto = new TestAggDTO { Id = 7 };
        _createHandlerMock
            .Setup(h => h.HandleAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));
        TestAggregateRootController sut = CreateController();

        ActionResult<TestAggDTO> result = await sut.CreateAsync(request, CancellationToken.None);

        var createdResult = result.Result as CreatedAtRouteResult;
        createdResult.Should().NotBeNull();
        createdResult!.StatusCode.Should().Be(StatusCodes.Status201Created);
        createdResult.Value.Should().Be(dto);
    }

    [Fact]
    public async Task CreateAsync_Success_RouteName_IsGetEntityNameById()
    {
        var request = new TestCreateRequest();
        var dto = new TestAggDTO { Id = 10 };
        _createHandlerMock
            .Setup(h => h.HandleAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));
        TestAggregateRootController sut = CreateController();

        ActionResult<TestAggDTO> result = await sut.CreateAsync(request, CancellationToken.None);

        var createdResult = result.Result as CreatedAtRouteResult;
        createdResult.Should().NotBeNull();
        createdResult!.RouteName.Should().Be($"Get{nameof(TestAggregateEntity)}ById");
        createdResult.RouteValues!["id"].Should().Be(10);
    }

    [Fact]
    public async Task CreateAsync_Failure_ReturnsHandleFailure()
    {
        var request = new TestCreateRequest();
        _createHandlerMock
            .Setup(h => h.HandleAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<TestAggDTO>(
                Error.Validation("Test.Validation", "Create failed")));
        TestAggregateRootController sut = CreateController();

        ActionResult<TestAggDTO> result = await sut.CreateAsync(request, CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // ── DeleteAsync ──
    [Fact]
    public async Task DeleteAsync_Success_ReturnsNoContent()
    {
        _deleteHandlerMock
            .Setup(h => h.HandleAsync(
                It.Is<DeleteEntityCommand<TestAggregateEntity, int>>(c => c.Id == 5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestAggregateRootController sut = CreateController();

        ActionResult result = await sut.DeleteAsync(5, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteAsync_Failure_ReturnsHandleFailure()
    {
        _deleteHandlerMock
            .Setup(h => h.HandleAsync(
                It.Is<DeleteEntityCommand<TestAggregateEntity, int>>(c => c.Id == 99),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                Error.NotFoundError("Test.NotFound", "Entity not found")));
        TestAggregateRootController sut = CreateController();

        ActionResult result = await sut.DeleteAsync(99, CancellationToken.None);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }
}

public sealed class TestAggregateRootController(
    IEntityQueryService<TestAggregateEntity, TestAggDTO, int> queryService,
    ICommandHandler<TestCreateRequest, Result<TestAggDTO>> createHandler,
    ICommandHandler<DeleteEntityCommand<TestAggregateEntity, int>, Result> deleteHandler,
    ILogger<EntityControllerBase<TestAggregateEntity, TestAggDTO, int>> logger)
    : AggregateRootEntityControllerBase<TestAggregateEntity, TestAggDTO, int, TestCreateRequest>(
        queryService, createHandler, deleteHandler, logger);

public sealed class TestAggregateEntity : AuditableAggregateRootEntity<int>;

public sealed record TestAggDTO : IBaseDTO<int>
{
    public required int Id { get; init; }
}

public sealed record TestCreateRequest : ICreateRequest;
