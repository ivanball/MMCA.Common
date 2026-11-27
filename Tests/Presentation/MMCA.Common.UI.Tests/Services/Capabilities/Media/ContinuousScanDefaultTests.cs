using AwesomeAssertions;
using MMCA.Common.UI.Services.Capabilities.Media;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Media;

/// <summary>
/// The default <see cref="IBarcodeScannerService.ScanContinuouslyAsync"/>, which every scanner without
/// a continuous surface of its own inherits: it loops <see cref="IBarcodeScannerService.ScanAsync"/>
/// until the scan comes back empty, hands over each new code once, and keeps the interface's "never
/// throws" promise for the scanner while letting the caller's own exception through. The MAUI session
/// needs a device and is not reachable from this project; the decision it shares with this loop is
/// pinned by <see cref="RepeatScanFilterTests"/>.
/// </summary>
public sealed class ContinuousScanDefaultTests
{
    [Fact]
    public async Task LoopsScanAsyncUntilItReturnsNull_HandingOverEachCode()
    {
        var scanner = new ScriptedScanner("ticket-1", "ticket-2", null);
        var handled = new List<string>();

        await Scan(scanner, (payload, _) =>
        {
            handled.Add(payload);
            return Task.FromResult<string?>("Checked in");
        });

        handled.Should().Equal("ticket-1", "ticket-2");
        scanner.Scans.Should().Be(3);
    }

    [Fact]
    public async Task TheSameCodeScannedAgainAtOnce_IsHandedOverOnce()
    {
        var scanner = new ScriptedScanner("ticket-1", "ticket-1", "ticket-2", null);
        var handled = new List<string>();

        await Scan(scanner, (payload, _) =>
        {
            handled.Add(payload);
            return Task.FromResult<string?>(null);
        });

        handled.Should().Equal("ticket-1", "ticket-2");
    }

    [Fact]
    public async Task AnExceptionFromTheCallback_EndsTheSession_AndReachesTheCaller()
    {
        var scanner = new ScriptedScanner("ticket-1", "ticket-2", null);

        var act = () => Scan(scanner, (_, _) => throw new InvalidOperationException("lookup failed"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("lookup failed");
        scanner.Scans.Should().Be(1, "the session ends at the failure");
    }

    [Fact]
    public async Task CancellingWhileTheCallbackRuns_EndsTheSessionQuietly()
    {
        using var cts = new CancellationTokenSource();
        var scanner = new ScriptedScanner("ticket-1", "ticket-2", null);

        await Scan(
            scanner,
            async (_, token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return null;
            },
            cts.Token);

        scanner.Scans.Should().Be(1);
    }

    [Fact]
    public async Task ANullCallback_Throws()
    {
        IBarcodeScannerService scanner = new ScriptedScanner((string?)null);

        var act = () => scanner.ScanContinuouslyAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // Called through the interface: the default implementation is not a member of the class.
    private static Task Scan(
        IBarcodeScannerService scanner,
        Func<string, CancellationToken, Task<string?>> onScanned,
        CancellationToken cancellationToken = default) =>
        scanner.ScanContinuouslyAsync(onScanned, cancellationToken);

    /// <summary>A scanner that answers each <see cref="ScanAsync"/> with the next scripted payload.</summary>
    private sealed class ScriptedScanner(params string?[] payloads) : IBarcodeScannerService
    {
        private readonly Queue<string?> _payloads = new(payloads);

        public int Scans { get; private set; }

        public bool IsSupported => true;

        public Task<bool> EnsureCameraAccessAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string?> ScanAsync(CancellationToken cancellationToken = default)
        {
            Scans++;
            return Task.FromResult(_payloads.Count > 0 ? _payloads.Dequeue() : null);
        }
    }
}
