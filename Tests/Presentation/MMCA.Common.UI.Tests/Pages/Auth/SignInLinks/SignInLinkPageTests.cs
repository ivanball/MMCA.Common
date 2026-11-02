using MMCA.Common.Testing.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages.Auth.SignInLinks;

/// <summary>
/// Runs the shared <see cref="SignInLinkPageTestsBase"/> facts over the framework's own
/// <c>/auth/sign-in-link</c> page, so the base the consumers subclass is exercised in this repo's CI.
/// </summary>
public sealed class SignInLinkPageTests : SignInLinkPageTestsBase;
