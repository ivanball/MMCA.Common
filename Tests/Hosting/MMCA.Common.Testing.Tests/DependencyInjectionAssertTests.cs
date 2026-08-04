using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Covers <see cref="DependencyInjectionAssert.ReturnsSameCollection"/>: it must pass for a genuinely
/// fluent registration and fail for one that hands back a different collection (the failure mode that
/// silently drops everything chained after it).
/// </summary>
public class DependencyInjectionAssertTests
{
    [Fact]
    public void ReturnsSameCollection_Passes_ForAFluentRegistration()
    {
        var assert = () => DependencyInjectionAssert.ReturnsSameCollection(
            services => services.AddSingleton<ISampleService, SampleService>());

        assert.Should().NotThrow();
    }

    [Fact]
    public void ReturnsSameCollection_Fails_WhenADifferentCollectionIsReturned()
    {
        var assert = () => DependencyInjectionAssert.ReturnsSameCollection(
            services =>
            {
                services.AddSingleton<ISampleService, SampleService>();
                return new ServiceCollection();
            });

        assert.Should().Throw<Exception>();
    }

    [Fact]
    public void ReturnsSameCollection_RejectsANullRegistration()
    {
        var assert = () => DependencyInjectionAssert.ReturnsSameCollection(null!);

        assert.Should().Throw<ArgumentNullException>();
    }

    private interface ISampleService;

    private sealed class SampleService : ISampleService;
}
