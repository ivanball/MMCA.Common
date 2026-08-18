using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Architecture.Tests.CancellationFixtures;

/// <summary>Compliant: every public async method takes a trailing <c>cancellationToken</c>.</summary>
public sealed class CompliantFixtureService
{
    /// <summary>A compliant Task-returning method.</summary>
    public Task<int> GetAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(id.Length + (cancellationToken.IsCancellationRequested ? 1 : 0));

    /// <summary>A compliant ValueTask-returning method whose only parameter is the token.</summary>
    public ValueTask DoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>Not async, so out of scope entirely.</summary>
    public int Count(string id) => id.Length;

    /// <summary>Non-public, so out of scope even though it is async and token-less.</summary>
    internal Task HiddenAsync() => Task.CompletedTask;
}

/// <summary>Offender: public async methods with no token at all.</summary>
public sealed class MissingTokenFixtureService
{
    /// <summary>Offender: a token-less async method with parameters.</summary>
    public Task<string> RunAsync(string id) => Task.FromResult(id);

    /// <summary>Offender: a token-less async method with no parameters (the token would be its only one).</summary>
    public Task PingAsync() => Task.CompletedTask;
}

/// <summary>Offender: the token is present but is not the last parameter.</summary>
public sealed class MisplacedTokenFixtureService
{
    /// <summary>Offender: the token leads instead of trailing.</summary>
    [SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "Deliberate: this fixture exists to prove the fitness function catches exactly this shape.")]
    public Task<bool> RunAsync(CancellationToken cancellationToken, string id) =>
        Task.FromResult(cancellationToken.IsCancellationRequested && id.Length > 0);
}

/// <summary>Offender: the token trails but carries a different name.</summary>
public sealed class MisnamedTokenFixtureService
{
    /// <summary>Offender: the trailing token is named <c>token</c>.</summary>
    public Task<bool> RunAsync(string id, CancellationToken token) =>
        Task.FromResult(token.IsCancellationRequested && id.Length > 0);
}

/// <summary>Offender used to prove the per-method exemption list suppresses exactly one entry.</summary>
public sealed class ExemptableFixtureService
{
    /// <summary>Offender unless listed in the exemptions.</summary>
    public Task<string> LegacyAsync(string id) => Task.FromResult(id);
}

/// <summary>
/// Auto-exempt: <c>MoveNextAsync</c> is an implicit implementation of <see cref="IAsyncEnumerator{T}"/>,
/// declared outside the map's assemblies, so its token-less signature is not this repo's to change.
/// </summary>
public sealed class ExternalContractFixtureService : IAsyncEnumerator<int>
{
    /// <inheritdoc />
    public int Current => 0;

    /// <inheritdoc />
    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
