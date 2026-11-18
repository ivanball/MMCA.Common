using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Testing.UI.Pages;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>
/// Runs the shared <see cref="DataGridListPageTestsBase{TPage, TDto}"/> facts over a consumer-shaped
/// <see cref="ProbeListPage"/>, so the base the consumers subclass is exercised in this repo's own CI
/// against the real <c>DataGridListPageBase</c> and <c>ListPageShell</c>.
/// </summary>
public sealed class DataGridListPageTests : DataGridListPageTestsBase<ProbeListPage, ProbeRow>
{
    private readonly ProbeListPageFetch _fetch;

    public DataGridListPageTests() => _fetch = new ProbeListPageFetch(Services);

    protected override string PageTitle => ProbeListPage.PageTitle;

    protected override string EmptyStateText => ProbeListPage.EmptyText;

    protected override string FirstRowMarker => ProbeListPageFetch.Rows[0].Name;

    protected override string? FirstRowHref => ProbeListPage.DetailHref(ProbeListPageFetch.Rows[0].Id);

    protected override IReadOnlyList<ProbeRow> CreateRows() => ProbeListPageFetch.Rows;

    protected override void SetupFetch(Func<Task<Result<(IReadOnlyList<ProbeRow> Items, int TotalItems)>>> fetch) =>
        _fetch.Setup(fetch);
}

/// <summary>
/// The client double and the row fixtures a <see cref="DataGridListPageTestsBase{TPage, TDto}"/>
/// subclass over a <see cref="ProbeListPage"/> (conforming or deliberately broken) shares.
/// </summary>
internal sealed class ProbeListPageFetch
{
    private readonly Mock<IProbeRowClient> _client = new();

    public ProbeListPageFetch(IServiceCollection services) => services.AddSingleton(_client.Object);

    public static IReadOnlyList<ProbeRow> Rows { get; } = [new(11, "Probe row eleven"), new(12, "Probe row twelve")];

    public void Setup(Func<Task<Result<(IReadOnlyList<ProbeRow> Items, int TotalItems)>>> fetch) =>
        _client
            .Setup(c => c.GetPagedAsync(
                It.IsAny<Dictionary<string, (string Operator, string Value)>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(fetch);
}
