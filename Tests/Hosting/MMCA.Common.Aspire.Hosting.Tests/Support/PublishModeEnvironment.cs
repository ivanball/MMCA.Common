using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Aspire.Hosting.Tests.Support;

/// <summary>
/// Resolves an AppHost resource's environment variables the way the tests in this project assert
/// on them.
/// </summary>
internal static class PublishModeEnvironment
{
    /// <summary>
    /// Resolves a resource's environment variables in publish mode, where every reference stays a
    /// manifest expression (for example <c>{servicebus.bindings.amqp.host}</c>) rather than a value
    /// DCP happened to allocate. That is what makes an assertion about WHICH endpoint a value is
    /// bound to, rather than about a port.
    /// </summary>
    public static async Task<Dictionary<string, string>> EnvironmentOf(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = services,
            });

        IExecutionConfigurationResult result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        result.Exception.Should().BeNull();
        return result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }
}
