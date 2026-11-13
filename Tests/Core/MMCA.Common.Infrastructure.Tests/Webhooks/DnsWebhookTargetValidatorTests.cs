using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Infrastructure.Webhooks;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <c>DnsWebhookTargetValidator</c> (design 35.20 to 35.22, review amendment A1), resolved as the
/// <see cref="IWebhookTargetValidator"/> that <c>AddWebhooks</c> registers, over a stub
/// <see cref="IWebhookDnsResolver"/> registered first (so no test touches real DNS). Without the
/// development switch a target must be https and EVERY address its host resolves to must be public;
/// with the switch (only in a Development host) neither the scheme nor the address is checked and
/// the host is never resolved. A null host environment is production.
/// </summary>
public sealed class DnsWebhookTargetValidatorTests
{
    [Fact]
    public async Task HostResolvingToPrivate_Fails()
    {
        var resolver = new StubDnsResolver("10.0.0.7");
        await using var provider = Build(resolver, Environments.Production, allowInsecureFlag: false);

        var result = await ValidateAsync(provider, "https://hooks.example.com/in");

        AssertRefused(result);
        resolver.ResolvedHosts.Should().ContainSingle().Which.Should().Be("hooks.example.com");
    }

    [Fact]
    public async Task OneOfTwoPrivate_Fails()
    {
        var resolver = new StubDnsResolver("8.8.8.8", "192.168.1.10");
        await using var provider = Build(resolver, Environments.Production, allowInsecureFlag: false);

        var result = await ValidateAsync(provider, "https://hooks.example.com/in");

        AssertRefused(result, "ANY private address refuses the target, since the connection may land on it");
    }

    [Fact]
    public async Task NoAddresses_Fails()
    {
        var resolver = new StubDnsResolver();
        await using var provider = Build(resolver, Environments.Production, allowInsecureFlag: false);

        var result = await ValidateAsync(provider, "https://hooks.example.com/in");

        AssertRefused(result, "a host that resolves to nothing cannot be proven public");
    }

    [Fact]
    public async Task Public_Succeeds()
    {
        var resolver = new StubDnsResolver("8.8.8.8", "2606:4700:4700::1111");
        await using var provider = Build(resolver, Environments.Production, allowInsecureFlag: false);

        var result = await ValidateAsync(provider, "https://hooks.example.com/in");

        result.IsSuccess.Should().BeTrue();
        resolver.ResolvedHosts.Should().ContainSingle().Which.Should().Be("hooks.example.com");
    }

    [Fact]
    public async Task Http_FailsInProduction()
    {
        var resolver = new StubDnsResolver("8.8.8.8");
        await using var provider = Build(resolver, Environments.Production, allowInsecureFlag: false);

        var result = await ValidateAsync(provider, "http://hooks.example.com/in");

        AssertRefused(result, "outside the development switch a target must be https");
    }

    [Fact]
    public async Task Http_AllowedInDevelopmentWithFlag()
    {
        var resolver = new StubDnsResolver("10.0.0.7");
        await using var provider = Build(resolver, Environments.Development, allowInsecureFlag: true);

        var result = await ValidateAsync(provider, "http://hooks.example.com/in");

        result.IsSuccess.Should().BeTrue();
        resolver.ResolvedHosts.Should().BeEmpty("with insecure targets allowed the validator does not resolve at all (rule 35.21)");
    }

    [Fact]
    public async Task Localhost_AllowedInDevelopmentWithFlag_WithoutResolving()
    {
        var resolver = new StubDnsResolver("127.0.0.1");
        await using var provider = Build(resolver, Environments.Development, allowInsecureFlag: true);

        var result = await ValidateAsync(provider, "http://localhost:5005/hook");

        result.IsSuccess.Should().BeTrue("A1: the development switch relaxes the address checks as well as the scheme");
        resolver.ResolvedHosts.Should().BeEmpty();
    }

    [Fact]
    public async Task PrivateIpLiteral_AllowedInDevelopmentWithFlag()
    {
        var resolver = new StubDnsResolver();
        await using var provider = Build(resolver, Environments.Development, allowInsecureFlag: true);

        var result = await ValidateAsync(provider, "http://10.0.0.5:5005/hook");

        result.IsSuccess.Should().BeTrue();
        resolver.ResolvedHosts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://localhost/hook")]
    [InlineData("http://localhost:5005/hook")]
    [InlineData("https://10.0.0.5/hook")]
    public async Task DevelopmentWithoutFlag_StillRefusesLocalhost(string target)
    {
        var resolver = new StubDnsResolver("127.0.0.1");
        await using var provider = Build(resolver, Environments.Development, allowInsecureFlag: false);

        var result = await ValidateAsync(provider, target);

        AssertRefused(result, "Development alone does not relax anything; the flag must be set too");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopmentWithFlag_FlagIsIgnored(string environmentName)
    {
        var resolver = new StubDnsResolver("127.0.0.1");
        await using var provider = Build(resolver, environmentName, allowInsecureFlag: true);

        (await ValidateAsync(provider, "http://localhost:5005/hook")).IsSuccess.Should().BeFalse(
            "the flag is honoured only in a Development host (rule 35.22)");
        (await ValidateAsync(provider, "https://hooks.example.com/in")).IsSuccess.Should().BeFalse(
            "and the host is still resolved and checked, here to a loopback address");
    }

    [Fact]
    public async Task NullEnvironment_IsProduction()
    {
        var resolver = new StubDnsResolver("10.0.0.7");
        await using var provider = Build(resolver, environmentName: null, allowInsecureFlag: true);

        var localhost = await ValidateAsync(provider, "http://localhost:5005/hook");
        var resolvesPrivate = await ValidateAsync(provider, "https://hooks.example.com/in");

        AssertRefused(localhost, "no registered IHostEnvironment reads as production (ADR-122)");
        AssertRefused(resolvesPrivate);
        resolver.ResolvedHosts.Should().Contain("hooks.example.com", "outside Development the host is resolved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FtpOrUserInfo_RefusedWhateverTheSwitch(bool development)
    {
        var resolver = new StubDnsResolver("8.8.8.8");
        await using var provider = Build(
            resolver,
            development ? Environments.Development : Environments.Production,
            allowInsecureFlag: development);

        AssertRefused(await ValidateAsync(provider, "ftp://hooks.example.com/in"));
        AssertRefused(await ValidateAsync(provider, "https://user:secret@hooks.example.com/in"));
    }

    [Fact]
    public async Task ProductionWithFlag_LogsOneWarning()
    {
        var logs = new CapturingLoggerProvider();
        var resolver = new StubDnsResolver("8.8.8.8");
        var services = CreateServices(resolver, Environments.Production, allowInsecureFlag: true, logs);
        await using var provider = services.BuildServiceProvider();

        services.Should().Contain(
            d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(InsecureWebhookTargetsNoticeService),
            "AddWebhooks adds the startup notice service (35(d))");
        await StartWebhookHostedServicesAsync(services, provider);
        await ValidateAsync(provider, "https://hooks.example.com/in");
        await ValidateAsync(provider, "https://hooks.example.com/in");

        logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("AllowInsecureTargetsInDevelopment", StringComparison.Ordinal))
            .Should().ContainSingle("a non-Development host with the flag set says once, at startup, that the flag is ignored");
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task NoWarning_WhenTheFlagIsHonouredOrUnset(string environmentName, bool allowInsecureFlag)
    {
        var logs = new CapturingLoggerProvider();
        var resolver = new StubDnsResolver("8.8.8.8");
        var services = CreateServices(resolver, environmentName, allowInsecureFlag, logs);
        await using var provider = services.BuildServiceProvider();

        await StartWebhookHostedServicesAsync(services, provider);
        await ValidateAsync(provider, "https://hooks.example.com/in");

        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning && e.Message.Contains("AllowInsecureTargetsInDevelopment", StringComparison.Ordinal));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertRefused(Result result, string because = "")
    {
        result.IsFailure.Should().BeTrue(because);
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(WebhookErrorCodes.TargetNotAllowed, because);
        result.Errors[0].Type.Should().Be(ErrorType.Validation, "TargetNotAllowed answers 400");
    }

    private static async Task<Result> ValidateAsync(ServiceProvider provider, string target)
    {
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IWebhookTargetValidator>();
        return await validator.ValidateAsync(new Uri(target), Ct);
    }

    private static ServiceProvider Build(StubDnsResolver resolver, string? environmentName, bool allowInsecureFlag) =>
        CreateServices(resolver, environmentName, allowInsecureFlag, logs: null).BuildServiceProvider();

    private static ServiceCollection CreateServices(
        StubDnsResolver resolver,
        string? environmentName,
        bool allowInsecureFlag,
        CapturingLoggerProvider? logs)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Webhooks:Enabled"] = "true",
                ["Webhooks:AllowInsecureTargetsInDevelopment"] = allowInsecureFlag ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        if (environmentName is not null)
        {
            services.AddSingleton(Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName));
        }

        // Registered before AddWebhooks, whose TryAddSingleton keeps it.
        services.AddSingleton<IWebhookDnsResolver>(resolver);
        services.AddWebhooks(configuration);
        return services;
    }

    // Starts only the webhook feature's own hosted services: the data-protection key ring service
    // AddDataProtection adds would touch the file system.
    private static async Task StartWebhookHostedServicesAsync(ServiceCollection services, ServiceProvider provider)
    {
        foreach (var descriptor in services.Where(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.Namespace == typeof(InsecureWebhookTargetsNoticeService).Namespace))
        {
            var hosted = (IHostedService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            await hosted.StartAsync(Ct);
        }
    }

    /// <summary>Answers every host with the same fixed addresses and records which hosts were asked.</summary>
    private sealed class StubDnsResolver(params string[] addresses) : IWebhookDnsResolver
    {
        private readonly ConcurrentQueue<string> _hosts = new();

        public IReadOnlyList<string> ResolvedHosts => [.. _hosts];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            _hosts.Enqueue(host);
            return Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
        }
    }

    /// <summary>Keeps every log line written through any logger it creates.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose()
        {
        }

        internal sealed record Entry(LogLevel Level, string Message);

        private sealed class CapturingLogger(ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
