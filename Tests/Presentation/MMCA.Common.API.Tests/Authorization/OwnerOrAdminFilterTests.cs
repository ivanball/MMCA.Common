using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
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

    // Defaults preserve the original hard-coded vocabulary: customer_id / Admin / id.
    private OwnerOrAdminFilter CreateFilter(OwnerOrAdminFilterOptions? options = null) =>
        new(_currentUserService.Object, Options.Create(options ?? new OwnerOrAdminFilterOptions()));

    // ── Admin passes through ──
    [Fact]
    public async Task OnActionExecutionAsync_AdminRole_PassesThrough()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");
        var (context, _) = CreateContext();
        var nextCalled = false;
        var sut = CreateFilter();

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
        var sut = CreateFilter();

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
        var sut = CreateFilter();

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
        var sut = CreateFilter();

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }

    // ── Configured vocabulary: bypass role ──
    [Fact]
    public async Task OnActionExecutionAsync_ConfiguredBypassRole_PassesThrough()
    {
        _currentUserService.Setup(s => s.Role).Returns("Organizer");
        var (context, _) = CreateContext(new RouteValueDictionary { ["userId"] = "99" });
        var nextCalled = false;
        var sut = CreateFilter(new OwnerOrAdminFilterOptions
        {
            OwnerClaimType = "UserId",
            BypassRole = "Organizer",
            RouteParameterName = "userId",
        });

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!));
        });

        nextCalled.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    // ── Configured vocabulary: owner claim + route parameter ──
    [Fact]
    public async Task OnActionExecutionAsync_ConfiguredClaimAndRouteParameter_EnforcesOwnership()
    {
        _currentUserService.Setup(s => s.Role).Returns("Attendee");
        _currentUserService.Setup(s => s.GetClaimValue<int>("UserId")).Returns(42);
        var options = new OwnerOrAdminFilterOptions
        {
            OwnerClaimType = "UserId",
            BypassRole = "Organizer",
            RouteParameterName = "userId",
        };

        // Matching userId passes through.
        var (ownContext, _) = CreateContext(new RouteValueDictionary { ["userId"] = "42" });
        var nextCalled = false;
        var sut = CreateFilter(options);
        await sut.OnActionExecutionAsync(ownContext, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(ownContext.HttpContext, ownContext.RouteData, ownContext.ActionDescriptor), [], null!));
        });
        nextCalled.Should().BeTrue();

        // A foreign userId is forbidden.
        var (foreignContext, _) = CreateContext(new RouteValueDictionary { ["userId"] = "99" });
        await sut.OnActionExecutionAsync(foreignContext, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(foreignContext.HttpContext, foreignContext.RouteData, foreignContext.ActionDescriptor), [], null!)));
        foreignContext.Result.Should().BeOfType<ForbidResult>();
    }
}
