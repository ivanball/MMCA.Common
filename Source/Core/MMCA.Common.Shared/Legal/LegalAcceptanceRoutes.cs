namespace MMCA.Common.Shared.Legal;

/// <summary>
/// The routes of the legal-acceptance endpoints, declared once so the API controller base and the UI
/// client cannot drift.
/// </summary>
public static class LegalAcceptanceRoutes
{
    /// <summary>
    /// The action template, relative to a controller routed at <c>Users</c> (the data-export
    /// precedent): <c>GET</c> reads the caller's standing, <c>POST</c> records an acceptance.
    /// </summary>
    public const string Action = "me/legal-acceptance";

    /// <summary>The full relative path the UI client calls: <c>Users/me/legal-acceptance</c>.</summary>
    public const string Path = "Users/" + Action;
}
