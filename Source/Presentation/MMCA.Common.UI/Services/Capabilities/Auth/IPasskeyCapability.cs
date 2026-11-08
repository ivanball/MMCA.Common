using System.Text.Json;

namespace MMCA.Common.UI.Services.Capabilities.Auth;

/// <summary>
/// The head's WebAuthn capability (ADR-042, ADR-139): runs the browser half of a passkey ceremony.
/// The browser heads register <see cref="BrowserPasskeyCapability"/> (<c>navigator.credentials</c>
/// through <c>passkeys.js</c>); every other head keeps <see cref="UnavailablePasskeyCapability"/>, so
/// a MAUI head hides the passkey affordances instead of offering a ceremony it cannot run.
/// </summary>
/// <remarks>
/// Options and credentials cross as JSON in the WebAuthn <c>*JSON</c> shapes (binary members
/// base64url-encoded), exactly what the API's passkey endpoints take and return, so nothing in between
/// has to understand them.
/// </remarks>
public interface IPasskeyCapability
{
    /// <summary>Gets a value indicating whether this head can run a passkey ceremony at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Creates a passkey (<c>navigator.credentials.create</c>) for the given creation options.</summary>
    /// <param name="options">The creation options from <c>auth/passkeys/register/options</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attestation, or <see langword="null"/> when the user cancelled or the browser cannot.</returns>
    Task<JsonElement?> CreateAsync(JsonElement options, CancellationToken cancellationToken = default);

    /// <summary>Asks for a passkey assertion (<c>navigator.credentials.get</c>) with the browser's own prompt.</summary>
    /// <param name="options">The request options from <c>auth/passkeys/signin/options</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assertion, or <see langword="null"/> when the user cancelled or the browser cannot.</returns>
    Task<JsonElement?> GetAsync(JsonElement options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts passkey autofill (conditional mediation): the browser offers the user's passkeys in the
    /// email field's suggestions, and <paramref name="onCredential"/> runs if one is picked.
    /// </summary>
    /// <param name="options">The request options from <c>auth/passkeys/signin/options</c>.</param>
    /// <param name="onCredential">Receives the assertion when the user picks a passkey.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the browser supports autofill and it was started.</returns>
    Task<bool> StartAutofillAsync(JsonElement options, Func<JsonElement, Task> onCredential, CancellationToken cancellationToken = default);

    /// <summary>Stops a pending autofill request, so a modal request can run.</summary>
    /// <returns>A task.</returns>
    Task CancelAutofillAsync();
}
