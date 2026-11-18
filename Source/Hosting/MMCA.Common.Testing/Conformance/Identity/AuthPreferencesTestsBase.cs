using System.Net;
using AwesomeAssertions;
using MMCA.Common.Testing.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Conformance.Identity;

/// <summary>
/// Integration facts for the per-user UI preference endpoints (<c>PUT/GET /Auth/preferences</c>,
/// ADR-027 culture / ADR-028 theme) against an app's in-process Identity host. Pinned here: the
/// preferences round-trip (with the theme stored exactly as sent), a <see langword="null"/> request
/// field leaves the other preference unchanged (partial persist), each authenticated user reads only
/// its own values, an unsupported culture and an unsupported theme are rejected, and an anonymous
/// read is refused with 401.
/// </summary>
/// <remarks>
/// The controller, the validator (<c>ChangePreferencesCommandValidatorBase</c>) and the handler are
/// the framework's, but each app composes them over its own <c>User</c> aggregate, registration
/// endpoint and token audience, so whether the shipped Identity host honours the contract is a
/// per-app question. A consumer keeps a small sealed subclass (the ADR-015 pattern) carrying its
/// xUnit <c>[Collection]</c> and implementing <see cref="RegisterAndAuthenticateAsync"/>, the one
/// thing that differs between apps: how a fresh account is created and how the client becomes it.
/// Because a test class has one base, the subclass restates whatever small auth helper its own
/// Identity test base would have provided.
/// </remarks>
/// <typeparam name="TFixture">The app's Identity integration fixture.</typeparam>
public abstract class AuthPreferencesTestsBase<TFixture> : IntegrationTestBase<TFixture>
    where TFixture : IIntegrationTestFixture
{
    /// <summary>The preference endpoint pair under test.</summary>
    protected const string PreferencesPath = "/Auth/preferences";

    protected AuthPreferencesTestsBase(TFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Registers a new account under <paramref name="email"/> through the app's own registration
    /// path and leaves <see cref="IntegrationTestBase{TFixture}.Client"/> authenticated as that
    /// account, so the next preference call reads and writes that user's own values. Called twice in
    /// one fact with different addresses, each call must switch the client to the newer account.
    /// </summary>
    /// <param name="email">The address the new account registers under, unique within the fact.</param>
    /// <returns>A task that completes once the client holds the new account's credential.</returns>
    protected abstract Task RegisterAndAuthenticateAsync(string email);

    /// <summary>
    /// The theme is sent capitalized on purpose: the validator compares themes case-insensitively and
    /// the aggregate stores the value as sent, so the read must return exactly <c>Dark</c>.
    /// </summary>
    [Fact]
    public async Task PutThenGetPreferences_RoundTripsCultureAndTheme()
    {
        await RegisterAndAuthenticateAsync("prefs-roundtrip@test.com").ConfigureAwait(false);

        using var put = await PutAsync(PreferencesPath, new { Culture = "es", Theme = "Dark" }).ConfigureAwait(false);
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var prefs = await GetAsync<PreferencesResponse>(PreferencesPath).ConfigureAwait(false);
        prefs.Should().NotBeNull();
        prefs.Culture.Should().Be("es");
        prefs.Theme.Should().Be("Dark");
    }

    [Fact]
    public async Task PutPreferences_NullField_LeavesOtherPreferenceUnchanged()
    {
        await RegisterAndAuthenticateAsync("prefs-merge@test.com").ConfigureAwait(false);

        // Persist culture only (theme null: leave unchanged).
        using var putCulture = await PutAsync(PreferencesPath, new { Culture = "es", Theme = (string?)null }).ConfigureAwait(false);
        putCulture.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Then persist theme only; the previously-set culture must survive.
        using var putTheme = await PutAsync(PreferencesPath, new { Culture = (string?)null, Theme = "dark" }).ConfigureAwait(false);
        putTheme.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var prefs = await GetAsync<PreferencesResponse>(PreferencesPath).ConfigureAwait(false);
        prefs.Should().NotBeNull();
        prefs.Culture.Should().Be("es");
        prefs.Theme.Should().Be("dark");
    }

    [Fact]
    public async Task GetPreferences_EachUserSeesOnlyOwnValues()
    {
        await RegisterAndAuthenticateAsync("prefs-user-a@test.com").ConfigureAwait(false);
        using var put = await PutAsync(PreferencesPath, new { Culture = "es", Theme = "dark" }).ConfigureAwait(false);
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A second, distinct user has never set preferences: the first user's values must not leak.
        await RegisterAndAuthenticateAsync("prefs-user-b@test.com").ConfigureAwait(false);

        var prefs = await GetAsync<PreferencesResponse>(PreferencesPath).ConfigureAwait(false);
        prefs.Should().NotBeNull();
        prefs.Culture.Should().BeNull();
        prefs.Theme.Should().BeNull();
    }

    [Fact]
    public async Task PutPreferences_WithUnsupportedCulture_ReturnsBadRequest()
    {
        await RegisterAndAuthenticateAsync("prefs-bad-culture@test.com").ConfigureAwait(false);

        using var response = await PutAsync(PreferencesPath, new { Culture = "zz-ZZ", Theme = (string?)null }).ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// The shared validator accepts only <c>light</c> or <c>dark</c> (any casing), so every app built
    /// on it rejects any other theme before the handler runs.
    /// </summary>
    [Fact]
    public async Task PutPreferences_WithUnsupportedTheme_ReturnsBadRequest()
    {
        await RegisterAndAuthenticateAsync("prefs-bad-theme@test.com").ConfigureAwait(false);

        using var response = await PutAsync(PreferencesPath, new { Culture = (string?)null, Theme = "rainbow" }).ConfigureAwait(false);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task GetPreferences_Anonymous_ReturnsUnauthorized()
    {
        ClearAuthentication();

        using var response = await Client.GetAsync(new Uri(PreferencesPath, UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>The body <c>GET /Auth/preferences</c> answers with.</summary>
    /// <param name="Culture">The stored culture, or <see langword="null"/> when never set.</param>
    /// <param name="Theme">The stored theme, or <see langword="null"/> when never set.</param>
    private sealed record PreferencesResponse(string? Culture, string? Theme);
}
