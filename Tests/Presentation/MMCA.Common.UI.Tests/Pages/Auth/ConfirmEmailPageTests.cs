using MMCA.Common.Testing.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// Runs the shared <see cref="ConfirmEmailPageTestsBase"/> facts over the framework's own
/// <c>/confirm-email</c> page, so the base the consumers subclass is exercised in this repo's CI.
/// </summary>
public sealed class ConfirmEmailPageTests : ConfirmEmailPageTestsBase;
