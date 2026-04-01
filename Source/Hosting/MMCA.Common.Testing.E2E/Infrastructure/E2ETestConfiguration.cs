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
    /// Slows down each Playwright action by this many milliseconds. Useful for watching tests visually.
    /// Set E2E_SLOWMO=1000 for a 1-second delay between actions.
    /// </summary>
    public static float SlowMo =>
        float.TryParse(Environment.GetEnvironmentVariable("E2E_SLOWMO"), out var s) ? s : 0;

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
