using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Globalization;

/// <summary>
/// Proves the MudBlazor built-in-text localization seam (ADR-027): <c>AddMudServices()</c> registers
/// no <see cref="MudLocalizer"/> of its own, so <c>AddUIShared</c>'s <c>TryAddTransient</c> is
/// authoritative and MudBlazor chrome resolves through the <c>MudTranslations</c> resource pair.
/// </summary>
public sealed class ResxMudLocalizerTests
{
    private static MudLocalizer ResolveMudLocalizer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:ApiEndpoint"] = "https://localhost:6001" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMudServices();
        services.AddUIShared(configuration);

        return services.BuildServiceProvider().GetRequiredService<MudLocalizer>();
    }

    // TryAddTransient only wins when AddMudServices registered no MudLocalizer of its own; if a
    // future MudBlazor version starts shipping one, this assertion fails and the registration must
    // switch to Replace.
    [Fact]
    public void AddUIShared_RegistersResxMudLocalizer_AndMudBlazorSuppliesNoDefault() =>
        Assert.Equal("ResxMudLocalizer", ResolveMudLocalizer().GetType().Name);

    [Fact]
    public void ResxMudLocalizer_ResolvesSpanishTranslation_ForBuiltInKey()
    {
        var localizer = ResolveMudLocalizer();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");
            var localized = localizer["MudDataGridPager_RowsPerPage"];

            Assert.False(localized.ResourceNotFound);
            Assert.Equal("Filas por página:", localized.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ResxMudLocalizer_ReportsResourceNotFound_ForUnknownKey_SoMudBlazorFallsBackToBuiltIns()
    {
        var localizer = ResolveMudLocalizer();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");
            Assert.True(localizer["NotARealMudBlazorKey"].ResourceNotFound);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
