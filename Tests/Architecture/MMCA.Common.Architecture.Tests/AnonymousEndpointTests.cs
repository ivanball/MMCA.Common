using MMCA.Common.API.Controllers;
using MMCA.Common.Testing.Architecture;
using MMCA.Common.UI;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// The framework's own anonymous-endpoint gate: the only controller actions MMCA.Common ships
/// without an authorization gate are the four credential-exchange endpoints, which cannot require a
/// token because issuing one is what they do. Anything else acquiring <c>[AllowAnonymous]</c> in
/// MMCA.Common.API or MMCA.Common.UI fails here until it is added to the list above and reviewed
/// with it.
/// </summary>
public sealed class AnonymousEndpointTests : AnonymousEndpointTestsBase
{
    protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
    [
        typeof(ApiControllerBase).Assembly,
        typeof(UISharedAssemblyReference).Assembly,
    ];

    protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints =>
    [
        // Login, register and refresh mint or rotate the token pair, so requiring one would be
        // circular; all three are throttled by the auth-ip rate-limit policy instead.
        "MMCA.Common.API.Controllers.AuthControllerBase.LoginAsync",
        "MMCA.Common.API.Controllers.AuthControllerBase.RegisterAsync",
        "MMCA.Common.API.Controllers.AuthControllerBase.RefreshAsync",
        // The OAuth completion code is the caller's only credential at this point: it is single-use
        // and burned on first exchange.
        "MMCA.Common.API.Controllers.OAuthControllerBase.ExchangeAsync",
        // Password recovery: the caller has lost the credential, so requiring one would be circular.
        // Both are throttled by the auth-ip policy, and forgot-password always answers 202 so it
        // reveals nothing about which addresses hold accounts.
        // The base is generic over the app command records, so its reflected FullName carries the
        // arity suffix.
        "MMCA.Common.API.Controllers.PasswordResetAuthControllerBase`2.ForgotPasswordAsync",
        "MMCA.Common.API.Controllers.PasswordResetAuthControllerBase`2.ResetPasswordAsync",
    ];

    // A floor, not an equality: 12 API controller types plus the routable UI pages. Removing a
    // scanned type is a failure rather than a quietly smaller scan.
    protected override int MinimumScannedTypes => 21;
}
