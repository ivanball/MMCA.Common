namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Environment-variable-driven configuration for E2E tests.
/// Each downstream project sets the <c>Default*</c> properties (via <c>[ModuleInitializer]</c>)
/// to provide app-specific defaults (e.g. admin email). Environment variables always take precedence.
/// </summary>
public static class E2ETestConfiguration
{
    public static string DefaultBaseUrl { get; set; } = "https://localhost:7108";

    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? DefaultBaseUrl;

    public static bool Headless =>
        !string.Equals(Environment.GetEnvironmentVariable("E2E_HEADLESS"), "false", StringComparison.OrdinalIgnoreCase);

    public static float DefaultTimeout =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_TIMEOUT"), out var t) ? t : 30_000;

    /// <summary>
    /// Timeout (ms) for the post-auth wait in <c>LoginAsync</c>/<c>RegisterNewUserAsync</c> — the slowest
    /// E2E step (full auth round-trip + forceLoad reload + re-render). On a contended CI runner the login
    /// can spike past the general <see cref="DefaultTimeout"/>, so it's tunable independently via
    /// <c>E2E_AUTH_TIMEOUT</c>; otherwise it inherits <see cref="DefaultTimeout"/>.
    /// </summary>
    public static float AuthTimeout =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_AUTH_TIMEOUT"), out var t) ? t : DefaultTimeout;

    /// <summary>
    /// Slows down each Playwright action by this many milliseconds. Useful for watching tests visually.
    /// Set E2E_SLOWMO=1000 for a 1-second delay between actions.
    /// </summary>
    public static float SlowMo =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_SLOWMO"), out var s) ? s : 0;

    /// <summary>
    /// Which Playwright browser engine to launch: <c>chromium</c> (default), <c>firefox</c>, or
    /// <c>webkit</c>. Set <c>E2E_BROWSER=firefox</c> to exercise the cross-browser support matrix;
    /// CI runs the suite once per engine. Unknown values fall back to Chromium.
    /// </summary>
    public static string Browser =>
        Environment.GetEnvironmentVariable("E2E_BROWSER") ?? "chromium";

    /// <summary>
    /// When set to a file path, captures a Playwright trace (full network log, DOM snapshots, console)
    /// for each test and writes it there on teardown. View it with
    /// <c>playwright show-trace &lt;path&gt;</c>. Unlike DevTools or slow-mo, the trace records at full
    /// speed, so it can inspect timing-sensitive failures that slowing down would mask. Set
    /// E2E_TRACE=C:\temp\trace.zip (run a single test so it isn't overwritten).
    /// </summary>
    public static string? TracePath =>
        Environment.GetEnvironmentVariable("E2E_TRACE") is { Length: > 0 } path ? path : null;

    public static class AdminCredentials
    {
        public static string DefaultEmail { get; set; } = "admin@localhost";
        public static string DefaultPassword { get; set; } = "Admin123!";

        public static string Email =>
            Environment.GetEnvironmentVariable("E2E_ADMIN_EMAIL") ?? DefaultEmail;

        public static string Password =>
            Environment.GetEnvironmentVariable("E2E_ADMIN_PASSWORD") ?? DefaultPassword;
    }

    public static class UserCredentials
    {
        public static string DefaultEmail { get; set; } = "user@localhost";
        public static string DefaultPassword { get; set; } = "User123!";

        public static string Email =>
            Environment.GetEnvironmentVariable("E2E_CUSTOMER_EMAIL") ?? DefaultEmail;

        public static string Password =>
            Environment.GetEnvironmentVariable("E2E_CUSTOMER_PASSWORD") ?? DefaultPassword;
    }
}
