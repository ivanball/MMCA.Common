namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// The WebAssembly half of the runtime configuration handshake: fetches the document the Server
/// host serves at <c>/client-config</c> (<c>MapClientConfigEndpoint</c> in
/// <c>MMCA.Common.UI.Web</c>) so the API base address is not baked into the static bundle.
/// </summary>
/// <remarks>
/// <para>
/// Usage in a WASM <c>Program.cs</c>:
/// <c>builder.Configuration.AddJsonStream(await MmcaClientConfigBootstrap.LoadAsync(new Uri(builder.HostEnvironment.BaseAddress)));</c>.
/// The document comes back as a buffered stream because browser fetch streams do not support the
/// synchronous reads <c>AddJsonStream</c> performs.
/// </para>
/// <para>
/// <b>Bounded, one retry, then loud.</b> Each attempt is bounded by <see cref="DefaultTimeout"/> so
/// a cold or unreachable backend cannot park the boot on the default 100-second HttpClient timeout
/// with no UI to explain it. One retry covers the common cold-start case where the first request
/// lands while the Server host is still warming. A second failure is rethrown on purpose: a client
/// that cannot read its configuration must fail loudly instead of booting against defaults and
/// pointing at the wrong API.
/// </para>
/// </remarks>
public static class MmcaClientConfigBootstrap
{
    /// <summary>The relative path the document is fetched from.</summary>
    public const string ClientConfigPath = "client-config";

    /// <summary>Gets the per-attempt timeout <see cref="LoadAsync(Uri, CancellationToken)"/> applies.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets the pause before the single retry.</summary>
    public static TimeSpan DefaultRetryDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Fetches <c>client-config</c> from the app's own origin with <see cref="DefaultTimeout"/> per
    /// attempt and one retry after <see cref="DefaultRetryDelay"/>.
    /// </summary>
    /// <param name="baseAddress">The app's base address (<c>builder.HostEnvironment.BaseAddress</c>).</param>
    /// <param name="cancellationToken">Cancels the fetch; a cancelled fetch is not retried.</param>
    /// <returns>The document, buffered, ready for <c>AddJsonStream</c>.</returns>
    /// <exception cref="HttpRequestException">Both attempts failed.</exception>
    /// <exception cref="TaskCanceledException">Both attempts timed out, or the caller cancelled.</exception>
    public static async Task<Stream> LoadAsync(Uri baseAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        using var client = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = DefaultTimeout,
        };

        return await LoadAsync(client, DefaultRetryDelay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches <c>client-config</c> with a caller-owned client (its <c>BaseAddress</c> and
    /// <c>Timeout</c> apply) and one retry after <paramref name="retryDelay"/>.
    /// </summary>
    /// <param name="client">The client to fetch with; not disposed here.</param>
    /// <param name="retryDelay">The pause before the single retry.</param>
    /// <param name="cancellationToken">Cancels the fetch; a cancelled fetch is not retried.</param>
    /// <returns>The document, buffered, ready for <c>AddJsonStream</c>.</returns>
    /// <exception cref="HttpRequestException">Both attempts failed.</exception>
    /// <exception cref="TaskCanceledException">Both attempts timed out, or the caller cancelled.</exception>
    public static async Task<Stream> LoadAsync(HttpClient client, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var configUri = new Uri(ClientConfigPath, UriKind.Relative);
        byte[] bytes;

        try
        {
            bytes = await client.GetByteArrayAsync(configUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || IsTimeout(ex, cancellationToken))
        {
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            bytes = await client.GetByteArrayAsync(configUri, cancellationToken).ConfigureAwait(false);
        }

        return new MemoryStream(bytes, writable: false);
    }

    // HttpClient reports its own timeout as a TaskCanceledException; the caller's cancellation looks
    // the same, so only the one the caller did not ask for is retried.
    private static bool IsTimeout(Exception exception, CancellationToken cancellationToken) =>
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}
