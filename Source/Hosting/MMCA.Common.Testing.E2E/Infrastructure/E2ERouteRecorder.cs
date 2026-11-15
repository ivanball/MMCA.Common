using System.Collections.Concurrent;
using System.Text.Json;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Records which pages and API paths an end-to-end test reaches, for the test-gap report (ADR-147).
/// Opt-in: <see cref="FromEnvironment"/> returns <see langword="null"/> unless <c>E2E_ROUTE_LOG_DIR</c>
/// names a folder, so a run that never sets it behaves exactly as before. When on, each test writes
/// one JSON Lines file (<c>&lt;test&gt;-&lt;guid&gt;.jsonl</c>), so tests running in parallel never share
/// a file, and each line is <c>{ test, kind, method, path }</c>.
/// <para>
/// <b>Only the path is written.</b> The query string and the fragment are dropped before anything is
/// recorded, because guest-order tokens and sign-in links travel there. Requests to any origin other
/// than the app's own (the UI host, plus <c>E2E_API_BASE_URL</c> when set) are ignored, so a payment
/// provider or a CDN never appears in the log.
/// </para>
/// </summary>
public sealed class E2ERouteRecorder
{
    private const string DirectoryVariable = "E2E_ROUTE_LOG_DIR";
    private const string ApiBaseUrlVariable = "E2E_API_BASE_URL";
    private const int MaxFileNameStem = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _directory;
    private readonly string _testName;
    private readonly Uri[] _origins;
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Creates a recorder that writes to <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder the test's log file is written to; created on flush.</param>
    /// <param name="testName">The test's display name, written on every line and used in the file name.</param>
    /// <param name="origins">The app's own origins; requests anywhere else are ignored.</param>
    public E2ERouteRecorder(string directory, string testName, IReadOnlyCollection<Uri> origins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);
        ArgumentNullException.ThrowIfNull(origins);

        _directory = directory;
        _testName = testName;
        _origins = [.. origins.Where(static origin => origin.IsAbsoluteUri)];
    }

    /// <summary>
    /// Creates a recorder when <c>E2E_ROUTE_LOG_DIR</c> is set, adding <c>E2E_API_BASE_URL</c> (when
    /// set) to the recorded origins; otherwise returns <see langword="null"/>.
    /// </summary>
    /// <param name="testName">The test's display name.</param>
    /// <param name="origins">The app's own origins, normally the UI base URL.</param>
    /// <returns>A recorder, or <see langword="null"/> when recording is off.</returns>
    public static E2ERouteRecorder? FromEnvironment(string testName, IReadOnlyCollection<Uri> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var allOrigins = origins.ToList();
        if (Uri.TryCreate(Environment.GetEnvironmentVariable(ApiBaseUrlVariable), UriKind.Absolute, out var api))
        {
            allOrigins.Add(api);
        }

        return new E2ERouteRecorder(directory, testName, allOrigins);
    }

    /// <summary>Records one navigation or request, keeping only its path, when it targets one of the app's origins.</summary>
    /// <param name="kind"><c>navigation</c> or <c>request</c>.</param>
    /// <param name="method">The HTTP method (<c>GET</c> for a navigation).</param>
    /// <param name="url">The absolute URL; its query string and fragment are never written.</param>
    public void Record(string kind, string method, Uri url)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || !Array.Exists(_origins, origin => IsSameOrigin(origin, url)))
        {
            return;
        }

        _lines.Enqueue(JsonSerializer.Serialize(new Entry(_testName, kind, method, url.AbsolutePath), JsonOptions));
    }

    /// <summary>Appends the recorded lines to this test's file. Writes nothing when nothing was recorded.</summary>
    /// <returns>A task that completes when the file is written.</returns>
    public Task FlushAsync()
    {
        if (_lines.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var lines = new List<string>();
        while (_lines.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{FileNameStem(_testName)}-{Guid.NewGuid():N}.jsonl");
        return File.AppendAllLinesAsync(path, lines);
    }

    private static bool IsSameOrigin(Uri origin, Uri url) =>
        Uri.Compare(origin, url, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string FileNameStem(string testName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string([.. testName.Select(c => Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c)]);
        return stem.Length <= MaxFileNameStem ? stem : stem[..MaxFileNameStem];
    }

    private sealed record Entry(string Test, string Kind, string Method, string Path);
}
