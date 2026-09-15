using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.UI.Globalization;
using MudBlazor;
using MudBlazor.Services;

namespace MMCA.Common.UI.Tests.Globalization;

/// <summary>
/// Proves the MudBlazor localization interceptor replacement (ADR-027 Decision 10): MudBlazor chrome
/// resolves exactly as it did through MudBlazor's default interceptor, but reading a built-in English
/// string never assigns <see cref="CultureInfo.CurrentUICulture"/>. That assignment is what pins a
/// MAUI hybrid head to its launch language: the renderer's thread carries the written
/// <c>AsyncLocal</c> forever, and a later switch of the thread defaults cannot override it.
/// </summary>
public sealed class InvariantMudLocalizationInterceptorTests
{
    private const string BuiltInKey = "MudDataGridPager_RowsPerPage";

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:ApiEndpoint"] = "https://localhost:6001" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddMudServices();
        services.AddUIShared(configuration);

        return services.BuildServiceProvider();
    }

    // Runs the probe on a dedicated thread so the culture assigned for the probe cannot leak into the
    // test runner's context, and so the ExecutionContext comparison sees only what the probe wrote.
    private static (ExecutionContext? Before, ExecutionContext? After, LocalizedString Result) ProbeUnderEnglish(
        ILocalizationInterceptor interceptor,
        string key)
    {
        ExecutionContext? before = null;
        ExecutionContext? after = null;
        LocalizedString? result = null;

        var thread = new Thread(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            before = ExecutionContext.Capture();
            result = interceptor.Handle(key);
            after = ExecutionContext.Capture();
        });

        thread.Start();
        thread.Join();

        return (before, after, result!);
    }

    [Fact]
    public void AddUIShared_ReplacesMudBlazorDefaultInterceptor()
    {
        using var provider = BuildProvider();

        var interceptor = provider.GetRequiredService<ILocalizationInterceptor>();

        Assert.IsType<InvariantMudLocalizationInterceptor>(interceptor);
    }

    [Fact]
    public void Handle_ReadsBuiltInEnglish_WithoutWritingTheAsyncLocalCulture()
    {
        using var provider = BuildProvider();
        var interceptor = provider.GetRequiredService<ILocalizationInterceptor>();

        var (before, after, result) = ProbeUnderEnglish(interceptor, BuiltInKey);

        Assert.False(result.ResourceNotFound);
        Assert.Equal("Rows per page:", result.Value);

        // An unchanged ExecutionContext instance is the proof: any AsyncLocal write, including one that
        // "restores" the previous culture, produces a new context.
        Assert.Same(before, after);
    }

    // Canary for the upstream behaviour this interceptor exists to avoid. MudBlazor's default reads
    // the built-in strings through an assign-invariant-then-restore of CurrentUICulture, and the
    // restore leaves the thread with an explicit AsyncLocal culture. When this test starts failing,
    // MudBlazor stopped assigning the culture and the replacement can be retired.
    [Fact]
    public void MudBlazorDefaultInterceptor_WritesTheAsyncLocalCulture_WhichIsWhyItIsReplaced()
    {
        var mudDefault = new DefaultLocalizationInterceptor(NullLoggerFactory.Instance, mudLocalizer: null);

        var (before, after, result) = ProbeUnderEnglish(mudDefault, BuiltInKey);

        Assert.Equal("Rows per page:", result.Value);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Handle_UsesMudTranslations_ForSpanish_AndFallsBackToBuiltInForUnknownKey()
    {
        using var provider = BuildProvider();
        var interceptor = provider.GetRequiredService<ILocalizationInterceptor>();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");

            var translated = interceptor.Handle(BuiltInKey);
            Assert.False(translated.ResourceNotFound);
            Assert.Equal("Filas por página:", translated.Value);

            var unknown = interceptor.Handle("NotARealMudBlazorKey");
            Assert.True(unknown.ResourceNotFound);
            Assert.Equal("NotARealMudBlazorKey", unknown.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Handle_FormatsArguments_IntoBuiltInStrings()
    {
        using var provider = BuildProvider();
        var interceptor = provider.GetRequiredService<ILocalizationInterceptor>();

        var (_, _, result) = ProbeUnderEnglish(interceptor, "MudPagination_CurrentPage");
        Assert.Equal("Current page {0}", result.Value);

        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Current page 3", interceptor.Handle("MudPagination_CurrentPage", 3).Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Handle_TreatsNeutralEnglishLikeAnyOtherCulture_AndStillResolves()
    {
        using var provider = BuildProvider();
        var interceptor = provider.GetRequiredService<ILocalizationInterceptor>();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            // The neutral "en" has the invariant culture as its parent, so MudBlazor's English shortcut
            // does not apply and the MudTranslations pair answers (its neutral resx is English).
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var result = interceptor.Handle(BuiltInKey);

            Assert.False(result.ResourceNotFound);
            Assert.Equal("Rows per page:", result.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
