using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.UI.Pages.Notifications;

namespace MMCA.Common.UI.Tests.Notifications;

/// <summary>
/// O-23 (ADC local test run 8): the authorization a notification page DECLARES (its
/// <c>[Authorize]</c> attributes, evaluated through the policies <c>AddUIShared</c> registers) is what
/// a full page load is answered with, because the Blazor Web host turns that metadata into the
/// endpoint's authorization. The history and compose pages (<see cref="NotificationList"/>,
/// <see cref="NotificationSend"/>) only work for a caller holding <c>notifications:manage</c>, so a
/// signed-in caller without it must be refused there (403, the in-shell Access Denied) rather than
/// served a 200 that renders Forbidden inside the page. The personal inbox
/// (<see cref="NotificationInbox"/>) stays open to every signed-in caller, and a host that hides the
/// notification pages keeps letting every caller through to the router's not-found answer.
/// <para>
/// The tests read the attributes off the page types rather than naming a policy, so the
/// implementation is free to choose how the permission requirement is expressed.
/// </para>
/// </summary>
public sealed class NotificationPageAuthorizationTests
{
    public static TheoryData<Type> ManagePages => [typeof(NotificationList), typeof(NotificationSend)];

    public static TheoryData<Type> AllNotificationPages =>
        [typeof(NotificationList), typeof(NotificationInbox), typeof(NotificationSend)];

    [Theory]
    [MemberData(nameof(ManagePages))]
    public async Task AManagePage_RefusesASignedInCaller_WithoutNotificationsManage(Type page)
    {
        var succeeded = await AuthorizeAsync(page, SignedIn(), hideNotificationPages: false);

        succeeded.Should().BeFalse(
            "a signed-in caller without notifications:manage must be refused at authorization (403 Access Denied), not served the page");
    }

    [Theory]
    [MemberData(nameof(ManagePages))]
    public async Task AManagePage_AdmitsASignedInCaller_WithNotificationsManage(Type page)
    {
        var succeeded = await AuthorizeAsync(page, SignedIn(NotificationPermissions.Manage), hideNotificationPages: false);

        succeeded.Should().BeTrue("notifications:manage is exactly what the history and compose pages need");
    }

    [Fact]
    public async Task TheInbox_StillAdmitsASignedInCaller_WithoutNotificationsManage()
    {
        var succeeded = await AuthorizeAsync(typeof(NotificationInbox), SignedIn(), hideNotificationPages: false);

        succeeded.Should().BeTrue("the inbox is every signed-in user's own notifications, not a management page");
    }

    [Theory]
    [MemberData(nameof(AllNotificationPages))]
    public async Task ANotificationPage_StillRefusesAnAnonymousCaller(Type page)
    {
        var succeeded = await AuthorizeAsync(page, new ClaimsPrincipal(new ClaimsIdentity()), hideNotificationPages: false);

        succeeded.Should().BeFalse("a signed-out caller is still challenged when the pages are served");
    }

    [Theory]
    [MemberData(nameof(AllNotificationPages))]
    public async Task WhenTheHostHidesTheNotificationPages_EveryCallerStillPasses(Type page)
    {
        var anonymous = await AuthorizeAsync(page, new ClaimsPrincipal(new ClaimsIdentity()), hideNotificationPages: true);
        var signedInWithoutManage = await AuthorizeAsync(page, SignedIn(), hideNotificationPages: true);

        anonymous.Should().BeTrue("a hidden page reaches the router's not-found answer instead of the sign-in challenge");
        signedInWithoutManage.Should().BeTrue("a hidden page is not-found for everyone, not Access Denied");
    }

    private static ClaimsPrincipal SignedIn(params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "Grace") };
        claims.AddRange(permissions.Select(permission => new Claim(AuthClaimTypes.Permission, permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static async Task<bool> AuthorizeAsync(Type page, ClaimsPrincipal user, bool hideNotificationPages)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiSettings:ApiEndpoint"] = "https://api.example.test/",
                ["Layout:HideNotificationPagesWhenUnregistered"] = hideNotificationPages ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUIShared(configuration);
        await using var provider = services.BuildServiceProvider();

        var authorizeData = page.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
        authorizeData.Should().NotBeEmpty($"{page.Name} must declare its authorization");

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);
        policy.Should().NotBeNull();

        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(user, resource: null, policy);
        return result.Succeeded;
    }
}
