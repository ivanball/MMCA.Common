namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Default implementation that disables all OAuth providers.
/// Downstream apps override this registration to enable specific providers.
/// </summary>
internal sealed class DefaultOAuthUISettings : IOAuthUISettings;
