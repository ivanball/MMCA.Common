using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Pages.Auth.TwoFactor;
using MMCA.Common.UI.TwoFactor;

namespace MMCA.Common.UI.Tests.TwoFactor;

/// <summary>
/// The authorization the account page declares, evaluated through the policies <c>AddUIShared</c>
/// registers (NotificationPageAuthorizationTests pattern): with the two-factor UI the page refuses a
/// signed-out caller (the sign-in challenge) and admits any signed-in one; without it every caller
/// passes, so the request reaches the router, which answers not-found.
/// </summary>
public sealed class TwoFactorPageAuthorizationTests
{
    [Fact]
    public async Task WithTheTwoFactorUI_ASignedOutCaller_IsRefused()
    {
        var succeeded = await AuthorizeAsync(Anonymous(), addTwoFactorUI: true);

        succeeded.Should().BeFalse("the account page is a signed-in page");
    }

    [Fact]
    public async Task WithTheTwoFactorUI_ASignedInCaller_IsAdmitted_WithoutAnyPermission()
    {
        var succeeded = await AuthorizeAsync(SignedIn(), addTwoFactorUI: true);

        succeeded.Should().BeTrue("every signed-in user manages their own second factor");
    }

    [Fact]
    public async Task WithoutTheTwoFactorUI_EveryCallerPasses_SoTheRouterAnswersNotFound()
    {
        var anonymous = await AuthorizeAsync(Anonymous(), addTwoFactorUI: false);
        var signedIn = await AuthorizeAsync(SignedIn(), addTwoFactorUI: false);

        anonymous.Should().BeTrue("a hidden page reaches the not-found answer instead of the sign-in challenge");
        signedIn.Should().BeTrue();
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedIn() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Grace")], "Test"));

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user, bool addTwoFactorUI)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiSettings:ApiEndpoint"] = "https://api.example.test/",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUIShared(configuration);
        if (addTwoFactorUI)
        {
            services.AddTwoFactorUI();
        }

        await using var provider = services.BuildServiceProvider();

        var authorizeData = typeof(TwoFactorAccount).GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
        authorizeData.Should().NotBeEmpty("the account page must declare its authorization");

        var policy = await AuthorizationPolicy.CombineAsync(provider.GetRequiredService<IAuthorizationPolicyProvider>(), authorizeData);
        policy.Should().NotBeNull();

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy);
        return result.Succeeded;
    }
}
