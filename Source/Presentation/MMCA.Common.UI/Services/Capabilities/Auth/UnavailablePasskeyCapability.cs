using System.Text.Json;

namespace MMCA.Common.UI.Services.Capabilities.Auth;

/// <summary>
/// Default <see cref="IPasskeyCapability"/>: no WebAuthn on this head. It is what every non-browser
/// head keeps (the MAUI head included, which has no native passkey support yet, ADR-139), so the
/// shared sign-in page and passkeys panel hide their passkey affordances there.
/// </summary>
public sealed class UnavailablePasskeyCapability : IPasskeyCapability
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<JsonElement?> CreateAsync(JsonElement options, CancellationToken cancellationToken = default) =>
        Task.FromResult<JsonElement?>(null);

    /// <inheritdoc />
    public Task<JsonElement?> GetAsync(JsonElement options, CancellationToken cancellationToken = default) =>
        Task.FromResult<JsonElement?>(null);

    /// <inheritdoc />
    public Task<bool> StartAutofillAsync(JsonElement options, Func<JsonElement, Task> onCredential, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public Task CancelAutofillAsync() => Task.CompletedTask;
}
