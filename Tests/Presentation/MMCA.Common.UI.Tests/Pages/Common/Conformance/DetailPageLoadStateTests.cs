using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI.Pages;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>
/// Runs the shared <see cref="DetailPageLoadStateTestsBase{TPage, TId, TDto}"/> facts over a
/// consumer-shaped <see cref="ProbeDetailPage"/>, so the base the consumers subclass is exercised in
/// this repo's own CI against the real <c>DetailPageBase</c> and <c>DetailLoadStateContent</c>.
/// </summary>
public sealed class DetailPageLoadStateTests : DetailPageLoadStateTestsBase<ProbeDetailPage, int, ProbeRecord>
{
    private readonly ProbeDetailPageFetch _fetch;

    public DetailPageLoadStateTests() => _fetch = new ProbeDetailPageFetch(Services);

    protected override int LoadedId => ProbeDetailPageFetch.LoadedId;

    protected override int OtherId => ProbeDetailPageFetch.OtherId;

    protected override string NotFoundText => ProbeDetailPage.NotFoundText;

    protected override string? LoadingLabel => ProbeDetailPage.LoadingText;

    protected override ProbeRecord CreateRecord(int id) => ProbeDetailPageFetch.Record(id);

    protected override string RecordMarker(int id) => ProbeDetailPageFetch.Record(id).Name;

    protected override void SetupFetch(int id, Func<Task<Result<ProbeRecord>>> fetch) => _fetch.Setup(id, fetch);
}

/// <summary>
/// The client double and the record fixtures a <see cref="DetailPageLoadStateTestsBase{TPage, TId, TDto}"/>
/// subclass over a <see cref="ProbeDetailPage"/> (conforming or deliberately broken) shares.
/// </summary>
internal sealed class ProbeDetailPageFetch
{
    public const int LoadedId = 7;

    public const int OtherId = 42;

    private readonly Mock<IProbeRecordClient> _client = new();

    public ProbeDetailPageFetch(IServiceCollection services) => services.AddSingleton(_client.Object);

    public static ProbeRecord Record(int id) => new(id, "Probe record " + id.ToString(CultureInfo.InvariantCulture));

    public void Setup(int id, Func<Task<Result<ProbeRecord>>> fetch) =>
        _client.Setup(c => c.GetAsync(id, It.IsAny<CancellationToken>())).Returns(fetch);
}
