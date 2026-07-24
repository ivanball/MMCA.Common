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

    private static (ActionExecutingContext Context, bool NextCalled) CreateContext(
        RouteValueDictionary? routeData = null,
        bool allowMissingOwner = false,
        Dictionary<string, object?>? actionArguments = null)
    {
        var httpContext = new DefaultHttpContext();
        var descriptor = new ActionDescriptor();
        if (allowMissingOwner)
        {
            descriptor.EndpointMetadata = [new AllowMissingOwnerAttribute()];
        }

        var actionContext = new ActionContext(httpContext, routeData is null ? new RouteData() : new RouteData(routeData), descriptor);
        var context = new ActionExecutingContext(actionContext, [], actionArguments ?? new Dictionary<string, object?>(), null!);
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
            OwnerParameterName = "userId",
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
            OwnerParameterName = "userId",
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

    // ── Deny by default when the owner parameter cannot be resolved ──
    // The filter used to call next() whenever TryGetOwnerParameter failed, so it stopped guarding
    // any action whose owner parameter was absent, non-integer, or carried inside a bound model.
    // "Nothing to compare" must read as deny, not as allow.
    [Fact]
    public async Task OnActionExecutionAsync_OwnerParameterMissing_ReturnsForbid()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext();
        var nextCalled = false;
        var sut = CreateFilter();

        await sut.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!));
        });

        context.Result.Should().BeOfType<ForbidResult>();
        nextCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("42abc")]
    [InlineData("")]
    public async Task OnActionExecutionAsync_OwnerParameterUnparseable_ReturnsForbid(string routeValue)
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext(new RouteValueDictionary { ["id"] = routeValue });
        var sut = CreateFilter();

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task OnActionExecutionAsync_ComplexBoundArgument_ReturnsForbid()
    {
        // A model-bound object whose ToString() is the type name must not read as a match.
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext(
            actionArguments: new Dictionary<string, object?> { ["id"] = new object() });
        var sut = CreateFilter();

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }

    // ── Explicit opt-out ──
    [Fact]
    public async Task OnActionExecutionAsync_AllowMissingOwner_PassesThroughWithoutOwnerParameter()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext(allowMissingOwner: true);
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

    [Fact]
    public async Task OnActionExecutionAsync_AllowMissingOwner_StillEnforcesAResolvableOwnerParameter()
    {
        // The opt-out excuses a missing parameter, not a foreign one: an action that does carry an
        // owner id is still checked against the caller's claim.
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);
        var (context, _) = CreateContext(new RouteValueDictionary { ["id"] = "99" }, allowMissingOwner: true);
        var sut = CreateFilter();

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task OnActionExecutionAsync_AllowMissingOwner_DoesNotExcuseAMissingOwnerClaim()
    {
        // The claim check runs before the parameter check and is unaffected by the opt-out.
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns((int?)null);
        var (context, _) = CreateContext(allowMissingOwner: true);
        var sut = CreateFilter();

        await sut.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(
                new ActionContext(context.HttpContext, context.RouteData, context.ActionDescriptor), [], null!)));

        context.Result.Should().BeOfType<ForbidResult>();
    }
}
