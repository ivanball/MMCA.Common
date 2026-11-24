using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Services.Diagnostics;

namespace MMCA.Common.UI.Tests.Services.Diagnostics;

/// <summary>
/// The options validation behind <c>AddInAppDiagnostics</c>: the <c>[Range]</c> on
/// <see cref="InAppDiagnosticsOptions.Capacity"/> is enforced when the options are first read, by
/// exactly one validator however often the call is repeated.
/// </summary>
public sealed class InAppDiagnosticsRegistrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    public void ACapacityOutsideTheRange_FailsWhenTheOptionsAreRead(int capacity)
    {
        var services = new ServiceCollection();
        services.AddInAppDiagnostics(options => options.Capacity = capacity);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<InAppDiagnosticsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().StartWith("Capacity: ").And.Contain("Capacity must be between 1 and 100000.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100_000)]
    public void ACapacityInsideTheRange_IsAccepted(int capacity)
    {
        var services = new ServiceCollection();
        services.AddInAppDiagnostics(options => options.Capacity = capacity);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<InAppDiagnosticsOptions>>().Value.Capacity.Should().Be(capacity);
    }

    [Fact]
    public void ARepeatedCall_RegistersOneValidator()
    {
        var services = new ServiceCollection();
        services.AddInAppDiagnostics();
        services.AddInAppDiagnostics();

        services.Where(d => d.ServiceType == typeof(IValidateOptions<InAppDiagnosticsOptions>)).Should().ContainSingle();
    }
}
