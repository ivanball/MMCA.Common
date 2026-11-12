using Amazon.Runtime;
using AwesomeAssertions;
using MMCA.Common.Aws.Telemetry;

namespace MMCA.Common.Aws.Tests.Telemetry;

/// <summary>
/// Rulings M1 and M2: the default-chain source keeps a success and never a failure, on both paths, and
/// its synchronous path resolves through the synchronous resolver instead of waiting on the
/// asynchronous lookup. The resolvers are injected, so nothing here reaches the real chain.
/// </summary>
public sealed class DefaultChainAwsTelemetryCredentialSourceTests
{
    private static readonly AWSCredentials Credentials = new BasicAWSCredentials("AKIDEXAMPLE", "secret");

    [Fact]
    public async Task ASuccess_IsKept_OnBothPaths()
    {
        var calls = 0;
        var source = new DefaultChainAwsTelemetryCredentialSource(
            () =>
            {
                Interlocked.Increment(ref calls);
                return Credentials;
            },
            () =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<AWSCredentials?>(Credentials);
            });

        (await source.GetAsync(TestContext.Current.CancellationToken)).Should().BeSameAs(Credentials);
        Synchronously(source).Should().BeSameAs(Credentials);
        (await source.GetAsync(TestContext.Current.CancellationToken)).Should().BeSameAs(Credentials);

        calls.Should().Be(1, "a resolved chain is kept; the SDK credentials refresh themselves");
    }

    [Fact]
    public void ASynchronousFailure_IsNotKept()
    {
        var calls = 0;
        var source = new DefaultChainAwsTelemetryCredentialSource(
            () => Interlocked.Increment(ref calls) == 1 ? throw new AmazonClientException("no credential in the chain") : Credentials,
            () => Task.FromResult<AWSCredentials?>(null));

        var first = () => source.Get(TestContext.Current.CancellationToken);

        first.Should().Throw<AmazonClientException>().WithMessage("no credential in the chain");
        source.Get(TestContext.Current.CancellationToken).Should().BeSameAs(Credentials);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task AnAsynchronousFailure_IsNotKept()
    {
        var calls = 0;
        var source = new DefaultChainAwsTelemetryCredentialSource(
            () => null,
            () => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<AWSCredentials?>(new AmazonClientException("no credential in the chain"))
                : Task.FromResult<AWSCredentials?>(Credentials));

        var first = async () => await source.GetAsync(TestContext.Current.CancellationToken);

        await first.Should().ThrowAsync<AmazonClientException>();
        (await source.GetAsync(TestContext.Current.CancellationToken)).Should().BeSameAs(Credentials);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task TheSynchronousPath_DoesNotWaitOnAPendingAsynchronousLookup()
    {
        var pending = new TaskCompletionSource<AWSCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DefaultChainAwsTelemetryCredentialSource(() => Credentials, () => pending.Task);
        var lookup = source.GetAsync(TestContext.Current.CancellationToken).AsTask();

        Synchronously(source).Should().BeSameAs(Credentials);

        pending.SetResult(null);
        await lookup;
    }

    [Fact]
    public void ANullResult_IsNotKept()
    {
        var calls = 0;
        var source = new DefaultChainAwsTelemetryCredentialSource(
            () => Interlocked.Increment(ref calls) == 1 ? null : Credentials,
            () => Task.FromResult<AWSCredentials?>(null));

        source.Get(TestContext.Current.CancellationToken).Should().BeNull();
        source.Get(TestContext.Current.CancellationToken).Should().BeSameAs(Credentials);
    }

    // The exporter's synchronous path, kept out of the async test bodies.
    private static AWSCredentials? Synchronously(DefaultChainAwsTelemetryCredentialSource source) =>
        source.Get(TestContext.Current.CancellationToken);
}
