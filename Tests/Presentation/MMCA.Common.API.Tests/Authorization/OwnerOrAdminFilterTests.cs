using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Interfaces.Infrastructure;
using Moq;

namespace MMCA.Common.API.Tests.Authorization;

public sealed class OwnerOrAdminFilterTests
{
    private readonly Mock<ICurrentUserService> _currentUserService = new();

    private static (ActionExecutingContext Context, bool NextCalled) CreateContext(RouteValueDictionary? routeData = null)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, routeData is null ? new RouteData() : new RouteData(routeData), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), null!);
        return (context, false);
    }

    // ── Admin passes through ──
    [Fact]
    public async Task OnActionExecutionAsync_AdminRole_PassesThrough()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");
        var (context, _) = CreateContext();
        var nextCalled = false;
        var sut = new OwnerOrAdminFilter(_currentUserService.Object);

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!));
        });

        nextCalled.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    // ── Owner passes through ──
    [Fact]
    public async Task OnActionExecutionAsync_OwnerMatchesRouteId_PassesThrough()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext(new RouteValueDictionary { ["id"] = "42" });
        var nextCalled = false;
        var sut = new OwnerOrAdminFilter(_currentUserService.Object);

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!));
        });

        nextCalled.Should().BeTrue();
    }

    // ── Non-owner gets 403 ──
    [Fact]
    public async Task OnActionExecutionAsync_NonOwner_ReturnsForbid()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext(new RouteValueDictionary { ["id"] = "99" });
        var sut = new OwnerOrAdminFilter(_currentUserService.Object);

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }

    // ── No customer_id claim gets 403 ──
    [Fact]
    public async Task OnActionExecutionAsync_NoCustomerIdClaim_ReturnsForbid()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns((int?)null);
        var (context, _) = CreateContext(new RouteValueDictionary { ["id"] = "42" });
        var sut = new OwnerOrAdminFilter(_currentUserService.Object);

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }
}
