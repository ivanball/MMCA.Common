using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "Grandfathered public overload released after the v1.152 baseline while RS0026/RS0027 were off; changing its signature is a breaking change (RS0026/RS0027 baseline)")]
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

        return new MemoryStream(ResolveSameOriginApiEndpoint(bytes, client.BaseAddress), writable: false);
    }

    /// <summary>
    /// A host running the same-origin API proxy serves <c>api.sameOriginApiEndpoint</c> as an
    /// origin-relative path (the server cannot know the public origin a browser used behind ingress), and
    /// the <c>"APIClient"</c> needs an absolute base address, so the path is resolved here against the
    /// address the document was fetched from. Any other document is returned byte for byte.
    /// </summary>
    internal static byte[] ResolveSameOriginApiEndpoint(byte[] document, Uri? baseAddress)
    {
        if (baseAddress is null || !baseAddress.IsAbsoluteUri)
        {
            return document;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(document, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // Not ours to judge: configuration binding reports a malformed document with its own error.
            return document;
        }

        if (root is not JsonObject rootObject
            || rootObject[ApiSettings.SectionName] is not JsonObject api
            || api[nameof(ApiSettings.SameOriginApiEndpoint)] is not JsonValue value
            || !value.TryGetValue<string>(out var path)
            || string.IsNullOrWhiteSpace(path)
            || IsHttpAbsolute(path))
        {
            return document;
        }

        api[nameof(ApiSettings.SameOriginApiEndpoint)] = new Uri(baseAddress, path).AbsoluteUri;
        return Encoding.UTF8.GetBytes(rootObject.ToJsonString());
    }

    /// <summary>
    /// True only for an absolute http(s) address. A bare <c>Uri.TryCreate(..., UriKind.Absolute, ...)</c>
    /// is not enough: on Unix-like runtimes, browser WebAssembly included, <c>"/api/"</c> parses as the
    /// absolute <c>file:///api/</c>, so the origin-relative path would be left unresolved and the
    /// <c>"APIClient"</c> would send every request to <c>file:///api/...</c>.
    /// </summary>
    internal static bool IsHttpAbsolute(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var absolute)
        && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);

    // HttpClient reports its own timeout as a TaskCanceledException; the caller's cancellation looks
    // the same, so only the one the caller did not ask for is retried.
    private static bool IsTimeout(Exception exception, CancellationToken cancellationToken) =>
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}
