using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;
using Moq;

namespace MMCA.Common.API.Tests.Controllers;

public sealed class CrudEntityControllerBaseTests
{
    /// <summary>The token the conditional-write filter decoded for the request under test.</summary>
    private static readonly byte[] IfMatchToken = [4, 2];

    private readonly Mock<IEntityQueryService<TestAggregateEntity, TestCrudDTO, int>> _queryServiceMock = new();
    private readonly Mock<ICommandHandler<TestCreateRequest, Result<TestCrudDTO>>> _createHandlerMock = new();
    private readonly Mock<ICommandHandler<UpdateEntityCommand<TestAggregateEntity, TestUpdateRequest, int>, Result<TestCrudDTO>>> _updateHandlerMock = new();
    private readonly Mock<ICommandHandler<DeleteEntityCommand<TestAggregateEntity, int>, Result>> _deleteHandlerMock = new();
    private readonly Mock<ILogger<EntityControllerBase<TestAggregateEntity, TestCrudDTO, int>>> _loggerMock = new();

    private TestCrudController CreateController()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ApplicationSettings { MaxPageSize = 100 }));

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        // [SupportsIfMatch] decodes the If-Match header into this slot before the action runs, so a
        // direct call to the action supplies it the same way.
        httpContext.Items[SupportsIfMatchAttribute.TokenItemKey] = IfMatchToken;

        return new TestCrudController(
            _queryServiceMock.Object,
            _createHandlerMock.Object,
            _updateHandlerMock.Object,
            _deleteHandlerMock.Object,
            _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private void SetupUpdate(Result<TestCrudDTO> result) =>
        _updateHandlerMock
            .Setup(h => h.HandleAsync(
                It.IsAny<UpdateEntityCommand<TestAggregateEntity, TestUpdateRequest, int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task UpdateAsync_Success_ReturnsOkWithTheRefreshedDTO()
    {
        var dto = new TestCrudDTO { Id = 5 };
        SetupUpdate(Result.Success(dto));
        TestCrudController sut = CreateController();

        ActionResult<TestCrudDTO> result = await sut.UpdateAsync(5, new TestUpdateRequest(), CancellationToken.None);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
        okResult.Value.Should().Be(dto);
    }

    [Fact]
    public async Task UpdateAsync_BuildsTheCommandFromTheRouteIdTheBodyAndTheIfMatchToken()
    {
        UpdateEntityCommand<TestAggregateEntity, TestUpdateRequest, int>? captured = null;
        _updateHandlerMock
            .Setup(h => h.HandleAsync(
                It.IsAny<UpdateEntityCommand<TestAggregateEntity, TestUpdateRequest, int>>(),
                It.IsAny<CancellationToken>()))
            .Callback((UpdateEntityCommand<TestAggregateEntity, TestUpdateRequest, int> c, CancellationToken _) => captured = c)
            .ReturnsAsync(Result.Success(new TestCrudDTO { Id = 5 }));
        var request = new TestUpdateRequest();
        TestCrudController sut = CreateController();

        await sut.UpdateAsync(5, request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(5);
        captured.Request.Should().Be(request);
        captured.RowVersion.Should().Equal(IfMatchToken, "the token comes from the header, never from the body");
    }

    [Fact]
    public async Task UpdateAsync_Success_EmitsTheRefreshedTokenAsAWeakETag()
    {
        SetupUpdate(Result.Success(new TestCrudDTO { Id = 5, RowVersion = [1, 2, 3] }));
        TestCrudController sut = CreateController();

        await sut.UpdateAsync(5, new TestUpdateRequest(), CancellationToken.None);

        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString()
            .Should().Be(ConcurrencyETag.Format([1, 2, 3]));
    }

    [Fact]
    public async Task UpdateAsync_Failure_ReturnsHandleFailure()
    {
        SetupUpdate(Result.Failure<TestCrudDTO>(Error.NotFoundError("Test.NotFound", "Entity not found")));
        TestCrudController sut = CreateController();

        ActionResult<TestCrudDTO> result = await sut.UpdateAsync(99, new TestUpdateRequest(), CancellationToken.None);

        var objectResult = result.Result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    // The inherited create and delete actions must keep working unchanged on the extended base.
    [Fact]
    public async Task DeleteAsync_IsStillInherited_AndReturnsNoContent()
    {
        _deleteHandlerMock
            .Setup(h => h.HandleAsync(
                It.Is<DeleteEntityCommand<TestAggregateEntity, int>>(c => c.Id == 5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        TestCrudController sut = CreateController();

        ActionResult result = await sut.DeleteAsync(5, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }
}

public sealed class TestCrudController(
    IEntityQueryService<TestAggregateEntity, TestCrudDTO, int> queryService,
    ICommandHandler<TestCreateRequest, Result<TestCrudDTO>> createHandler,
    ICommandHandler<UpdateEntityCommand<TestAggregateEntity, TestUpdateRequest, int>, Result<TestCrudDTO>> updateHandler,
    ICommandHandler<DeleteEntityCommand<TestAggregateEntity, int>, Result> deleteHandler,
    ILogger<EntityControllerBase<TestAggregateEntity, TestCrudDTO, int>> logger)
    : CrudEntityControllerBase<TestAggregateEntity, TestCrudDTO, int, TestCreateRequest, TestUpdateRequest>(
        queryService, createHandler, updateHandler, deleteHandler, logger);

public sealed record TestUpdateRequest;

public sealed record TestCrudDTO : IBaseDTO<int>, IConcurrencyAware
{
    public required int Id { get; init; }

    public byte[] RowVersion { get; init; } = [];
}
