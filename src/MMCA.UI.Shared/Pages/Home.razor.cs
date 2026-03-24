using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MMCA.UI.Shared.Pages;

/// <summary>
/// Landing page displaying conference countdown, keynote, tracks, sponsors, and location.
/// Fetches the default published event from the API to drive the countdown timer.
/// </summary>
public sealed partial class Home : IDisposable
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeOnly EventStartTime = new(8, 0, 0);

    [Inject]
    private IHttpClientFactory HttpClientFactory { get; set; } = default!;

    private CancellationTokenSource? _cts;
    private Timer? _countdownTimer;
    private TimeSpan _timeRemaining;
    private HomeEventInfo? _event;
    private bool _isLoading = true;
    private bool _disposed;

    private string EventName => _event?.Name ?? "Atlanta Cloud + AI Conference";

    private string EventDescription => _event?.Description
        ?? "The Atlanta Cloud + AI Conference draws upon the expertise of local and regional administrators, architects, and experts who come together to share their real-world experiences, lessons learned, best practices, and general knowledge with other interested individuals.";

    private string VenueAddress => _event?.VenueAddress ?? "FCS Innovation Academy, 125 Milton Ave, Alpharetta, GA 30009";

    private string MapEmbedUrl => _event?.VenueMapUrl
        ?? $"https://maps.google.com/maps?q={Uri.EscapeDataString(VenueAddress)}&t=&z=15&ie=UTF8&iwloc=&output=embed";

    protected override async Task OnInitializedAsync()
    {
        _cts = new CancellationTokenSource();
        await LoadEventAsync();
        _countdownTimer = new Timer(OnTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private async Task LoadEventAsync()
    {
        try
        {
            var client = HttpClientFactory.CreateClient("APIClient");
            var result = await client.GetFromJsonAsync<HomeCollectionResult>("events", ApiJsonOptions, _cts!.Token);
            _event = result?.Items?.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            // Component disposed during loading — expected
        }
        catch (HttpRequestException)
        {
            // API unavailable — use fallback defaults
        }
        finally
        {
            _isLoading = false;
            UpdateCountdown();
        }
    }

    private void OnTimerTick(object? state)
    {
        if (_disposed)
            return;

        UpdateCountdown();
        _ = InvokeAsync(StateHasChanged);
    }

    private void UpdateCountdown()
    {
        var targetDate = _event?.StartDate ?? new DateOnly(2026, 5, 30);
        var targetLocal = targetDate.ToDateTime(EventStartTime);
        var timeZoneId = _event?.TimeZone ?? "America/New_York";

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetLocal, tz);
            _timeRemaining = targetUtc - DateTime.UtcNow;
        }
        catch (TimeZoneNotFoundException)
        {
            _timeRemaining = targetLocal - DateTime.UtcNow;
        }

        if (_timeRemaining < TimeSpan.Zero)
            _timeRemaining = TimeSpan.Zero;
    }

    private string FormatEventDate()
    {
        var date = _event?.StartDate ?? new DateOnly(2026, 5, 30);
        return date.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _countdownTimer?.Change(-1, -1);
        _countdownTimer?.Dispose();
    }

    // ── API response models ──
    private sealed record HomeCollectionResult(List<HomeEventInfo>? Items);

    private sealed record HomeEventInfo(
        int Id,
        string Name,
        string? Description,
        DateOnly StartDate,
        DateOnly EndDate,
        string TimeZone,
        string? VenueAddress,
        string? VenueMapUrl);

    // ── Static landing page content ──
    private static readonly KeynoteSpeakerInfo Keynote = new(
        "Miguel Wood",
        "Enterprise Cloud Solutions Architect and AI/Data Expert",
        [
            "Miguel Wood is an Enterprise Cloud Solutions Architect and AI/Data expert with over 20 years of experience helping organizations modernize technology, data platforms, and business processes. He has led large-scale cloud migrations, AI initiatives, and enterprise transformation programs for Fortune-level companies and growing businesses, with a strong focus on Microsoft Azure.",
            "He has held senior leadership and chief architect roles, guiding organizations to align technology strategy with real business outcomes. Known for translating complex technical concepts into clear, actionable strategies, he bridges the gap between executive vision and engineering execution.",
            "He is also an entrepreneur and U.S. Army veteran, bringing a results-driven, practical approach to every engagement."
        ]);

    private static readonly ConferenceTrackInfo[] Tracks =
    [
        new("AI Applications & Intelligent Systems", Icons.Material.Filled.Psychology,
            "LLM-powered applications, RAG, AI copilots, AI agents, Prompt engineering, Tool-using AI, AI evaluation, Guardrails"),
        new("AI Infrastructure & Model Operations", Icons.Material.Filled.Memory,
            "Model deployment, LLM inference optimization, GPU infrastructure, AI workload orchestration, Model serving, AI observability"),
        new("Data Platforms for AI", Icons.Material.Filled.Storage,
            "Vector databases, Lakehouse architectures, Streaming data, Feature stores, Data quality, Metadata and lineage, Data governance"),
        new("Cloud Native Architecture", Icons.Material.Filled.Cloud,
            "Microservices, Event-driven systems, API-first, Service mesh, Workflow orchestration, Internal developer platforms"),
        new("Kubernetes & Workload Orchestration", Icons.Material.Filled.ViewInAr,
            "Kubernetes architecture, Cluster scaling, Multi-cluster ops, Operator patterns, GPU scheduling, Platform networking"),
        new("Serverless & Event Platforms", Icons.Material.Filled.FlashOn,
            "FaaS architectures, Event streaming, Event-driven microservices, Durable workflows, Serverless AI inference"),
        new("Cloud Infrastructure & IaC", Icons.Material.Filled.CloudQueue,
            "IaC patterns, GitOps, Immutable infrastructure, Multi-cloud, Resource automation, Cloud networking, Self-service platforms"),
        new("Observability, Reliability & SRE", Icons.Material.Filled.Visibility,
            "Observability platforms, OpenTelemetry, Distributed tracing, SLOs, Incident management, Chaos engineering"),
        new("Security, Identity & AI Safety", Icons.Material.Filled.Security,
            "Zero trust, IAM, Secrets management, Supply chain security, Runtime threat detection, AI model security, Prompt injection"),
        new("Multi-Cloud, Edge & Hybrid Systems", Icons.Material.Filled.DeviceHub,
            "Hybrid cloud, Edge computing, IoT platforms, Multi-cloud orchestration, Edge AI inference"),
        new("Professional Development & Soft Skills", Icons.Material.Filled.Groups,
            "Career management, Team building, Leadership, Tech consulting, Resume building, Public speaking, Managing change"),
        new("Cloud & AI Foundations", Icons.Material.Filled.School,
            "Intro to cloud, Containers basics, Intro to AI/ML, First cloud app, Prompt engineering fundamentals, DevOps basics, Career paths"),
    ];

    private static readonly SponsorTierInfo[] SponsorTiers =
    [
        new("Platinum", "#E5E4E2"),
        new("Gold", "#FFD700"),
        new("Silver", "#C0C0C0"),
        new("Swag", "#FF9800"),
    ];

    private static string GetTierIcon(string tierName) => tierName switch
    {
        "Platinum" => Icons.Material.Filled.Diamond,
        "Gold" => Icons.Material.Filled.EmojiEvents,
        "Silver" => Icons.Material.Filled.MilitaryTech,
        "Swag" => Icons.Material.Filled.CardGiftcard,
        _ => Icons.Material.Filled.Star,
    };

    private sealed record KeynoteSpeakerInfo(string Name, string Title, string[] BioParagraphs);
    private sealed record ConferenceTrackInfo(string Name, string Icon, string Topics);
    private sealed record SponsorTierInfo(string Name, string Color);
}
