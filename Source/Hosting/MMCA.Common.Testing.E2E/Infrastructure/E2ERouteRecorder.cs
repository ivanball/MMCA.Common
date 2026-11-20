using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Records which pages and API paths an end-to-end test reaches, for the test-gap report (ADR-147).
/// Opt-in: <see cref="FromEnvironment"/> returns <see langword="null"/> unless <c>E2E_ROUTE_LOG_DIR</c>
/// names a folder, so a run that never sets it records nothing. When on, each test writes
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

    /// <summary>
    /// Attaches a recorder to a test's page and context when <c>E2E_ROUTE_LOG_DIR</c> is set; otherwise
    /// attaches nothing and returns <see langword="null"/>. The test name comes from the running xUnit
    /// test, and the log is flushed when the page or the context closes, so a base class needs exactly
    /// one call and no disposal code.
    /// </summary>
    /// <param name="baseUrl">The UI base URL; an unparsable value attaches nothing rather than failing the test.</param>
    /// <param name="page">The test's page; its main-frame navigations are recorded.</param>
    /// <param name="context">The test's browser context; its non-navigation requests are recorded.</param>
    /// <returns>The attached recorder, or <see langword="null"/> when recording is off.</returns>
    public static E2ERouteRecorder? AttachIfEnabled(string baseUrl, IPage page, IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin))
        {
            return null;
        }

        var recorder = FromEnvironment(TestContext.Current.Test?.TestDisplayName ?? "unknown-test", [origin]);
        recorder?.Attach(page, context);
        return recorder;
    }

    /// <summary>
    /// Records <paramref name="page"/>'s main-frame navigations (Blazor's client-side, history API ones
    /// included, which issue no request) and <paramref name="context"/>'s non-navigation requests (the
    /// API calls), so nothing is logged twice; flushes when either closes.
    /// </summary>
    /// <param name="page">The page to observe.</param>
    /// <param name="context">The browser context to observe.</param>
    public void Attach(IPage page, IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        page.FrameNavigated += (_, frame) => OnNavigated(frame);
        context.Request += (_, request) => OnRequest(request);
        page.Close += (_, _) => Flush();
        context.Close += (_, _) => Flush();
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
        var lines = DrainLines();
        return lines.Count == 0 ? Task.CompletedTask : File.AppendAllLinesAsync(NewLogPath(), lines);
    }

    // Playwright raises Close on its own dispatch thread with no awaiter, so the close-time flush is
    // synchronous; draining the queue makes a second close (page, then context) write nothing.
    private void Flush()
    {
        var lines = DrainLines();
        if (lines.Count > 0)
        {
            File.AppendAllLines(NewLogPath(), lines);
        }
    }

    private void OnNavigated(IFrame frame)
    {
        if (frame.ParentFrame is null && Uri.TryCreate(frame.Url, UriKind.Absolute, out var url))
        {
            Record("navigation", "GET", url);
        }
    }

    private void OnRequest(IRequest request)
    {
        if (!request.IsNavigationRequest && Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
        {
            Record("request", request.Method, url);
        }
    }

    private List<string> DrainLines()
    {
        var lines = new List<string>();
        while (_lines.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }

    private string NewLogPath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, $"{FileNameStem(_testName)}-{Guid.NewGuid():N}.jsonl");
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
