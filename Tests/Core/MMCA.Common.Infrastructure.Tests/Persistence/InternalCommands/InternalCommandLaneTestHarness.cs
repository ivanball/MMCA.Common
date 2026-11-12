using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// Scaffolding for the delivery-lane tests (design-webhook-lane.md 1(c), "Harness extension"): a
/// not-started <see cref="InternalCommandProcessor"/> built through its public constructor by
/// <see cref="InternalCommandTestHarness.CreateProcessor"/>, plus an
/// <see cref="InternalCommandLaneProcessor"/> over the SAME context (Phase 1 is sequential, so one
/// context suffices), a test lane over <see cref="LaneProbeCommand"/> keyed by its receiver, and a
/// handler whose per-receiver behaviour the test sets. Kept beside the shared harness rather than in
/// it, so the harness the equivalence suite runs on stays byte-identical.
/// </summary>
internal static class InternalCommandLaneTestHarness
{
    /// <summary>The test lane's name, emitted as the <c>lane</c> metric tag and log value.</summary>
    public const string LaneName = "probe";

    /// <summary>The text a charged attempt stores in <c>LastError</c> (design L1.10, open question 8).</summary>
    public const string CoolingDownError = "InternalCommands.ReceiverCoolingDown: Receiver cooling down after a failed delivery.";

    /// <summary>Gets the stored name of <see cref="LaneProbeCommand"/>, the command type the test lane claims.</summary>
    public static string ProbeCommandType { get; } = InternalCommandNameResolver.GetStorageName(typeof(LaneProbeCommand));

    /// <summary>Builds the test lane: rows of <see cref="LaneProbeCommand"/>, keyed by their receiver.</summary>
    /// <param name="cooldown">The configured receiver cooldown; 60 seconds when omitted.</param>
    /// <param name="commandType">The stored command type the lane claims; the probe's own when omitted.</param>
    /// <returns>The lane.</returns>
    public static InternalCommandLane ProbeLane(TimeSpan? cooldown = null, string? commandType = null) =>
        new(
            LaneName,
            commandType ?? ProbeCommandType,
            cooldown ?? TimeSpan.FromSeconds(60),
            command => ((LaneProbeCommand)command).Receiver);

    /// <summary>
    /// Builds a lane processor over <paramref name="context"/>. The returned provider is what both the
    /// lane and the row runner create their scopes from; it registers the probe handler (unless
    /// <paramref name="registerHandler"/> is false), the probe log, and an <see cref="ICorrelationContext"/>
    /// whose construction the log counts, because only an EXECUTION scope resolves one
    /// (<c>AmbientOrigin.Restore</c>), so the count is the number of execution scopes created.
    /// </summary>
    public static LaneUnderTest CreateLaneProcessor(
        ApplicationDbContext context,
        InternalCommandsSettings settings,
        FakeTimeProvider timeProvider,
        LaneProbeLog log,
        InternalCommandLane? lane = null,
        Action<IServiceCollection>? configure = null,
        ILogger<InternalCommandLaneProcessor>? logger = null,
        ILogger<InternalCommandProcessor>? rowsLogger = null,
        IInternalCommandSignal? signal = null,
        TenancySettings? tenancy = null,
        bool registerHandler = true)
    {
        (InternalCommandProcessor rows, ServiceProvider scopeServices) = InternalCommandTestHarness.CreateProcessor(
            context,
            settings,
            timeProvider,
            services =>
            {
                services.AddSingleton(log);
                services.AddScoped<ICorrelationContext>(_ =>
                {
                    log.CountExecutionScope();
                    return new CorrelationContext();
                });

                if (registerHandler)
                {
                    services.AddScoped<ICommandHandler<LaneProbeCommand, Result>, LaneProbeHandler>();
                }

                configure?.Invoke(services);
            },
            rowsLogger);

        var tableTargets = new FrameworkTableTargets(
            new EmptyEntityDataSourceRegistry(),
            new DefaultDataSourceResolver(),
            tenancy is null ? null : Options.Create(tenancy));

        var laneProcessor = new InternalCommandLaneProcessor(
            rows,
            scopeServices.GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<InternalCommandLaneProcessor>.Instance,
            signal ?? Mock.Of<IInternalCommandSignal>(),
            tableTargets,
            timeProvider,
            lane ?? ProbeLane());

        return new LaneUnderTest(laneProcessor, rows, scopeServices);
    }
}

/// <summary>A lane processor under test, the row runner it was given, and the provider both resolve from.</summary>
/// <param name="Lane">The lane processor.</param>
/// <param name="Rows">The not-started shared processor whose row methods the lane calls.</param>
/// <param name="ScopeServices">The provider behind every scope the lane and the row runner create.</param>
internal sealed record LaneUnderTest(
    InternalCommandLaneProcessor Lane,
    InternalCommandProcessor Rows,
    ServiceProvider ScopeServices) : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        Lane.Dispose();
        Rows.Dispose();
        ScopeServices.Dispose();
    }
}

/// <summary>A delivery stand-in: one row per receiver, keyed by <paramref name="Receiver"/>.</summary>
/// <param name="Receiver">The receiver key the test lane reads.</param>
/// <param name="Value">Free-form payload that identifies the row in the probe log.</param>
internal sealed record LaneProbeCommand(string Receiver, string Value) : IInternalCommand;

/// <summary>One handler invocation the probe saw.</summary>
/// <param name="Receiver">The receiver of the row.</param>
/// <param name="Value">The row's payload value.</param>
/// <param name="At">The fake-clock instant the invocation started.</param>
internal sealed record LaneProbeInvocation(string Receiver, string Value, DateTimeOffset At);

/// <summary>
/// Shared sink and per-receiver behaviour switch for <see cref="LaneProbeHandler"/>. A receiver with no
/// behaviour set succeeds. The log also counts concurrent invocations and execution scopes.
/// </summary>
/// <param name="clock">The fake clock the invocation instants are read from.</param>
internal sealed class LaneProbeLog(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Func<LaneProbeCommand, CancellationToken, Task<Result>>> _behaviours =
        new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<LaneProbeInvocation> _invocations = new();
    private int _inFlight;
    private int _maxInFlight;
    private int _executionScopes;

    /// <summary>Gets every invocation, in order.</summary>
    public IReadOnlyList<LaneProbeInvocation> Invocations => [.. _invocations];

    /// <summary>Gets the most invocations that were running at the same time.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Gets how many execution scopes have been created (see the harness).</summary>
    public int ExecutionScopes => Volatile.Read(ref _executionScopes);

    /// <summary>The failure a failing receiver returns: what the delivery handler returns for a 503.</summary>
    /// <returns>A failed result.</returns>
    public static Result FailedDelivery() =>
        Result.Failure(Error.Failure("Probe.DeliveryFailed", "The receiver answered 503."));

    /// <summary>The values of the rows sent to <paramref name="receiver"/>, in order.</summary>
    /// <param name="receiver">The receiver.</param>
    /// <returns>The values.</returns>
    public IReadOnlyList<string> ValuesFor(string receiver) =>
        [.. _invocations.Where(i => string.Equals(i.Receiver, receiver, StringComparison.Ordinal)).Select(i => i.Value)];

    /// <summary>Sets what the handler does for <paramref name="receiver"/>.</summary>
    /// <param name="receiver">The receiver.</param>
    /// <param name="behaviour">The behaviour.</param>
    public void Behave(string receiver, Func<LaneProbeCommand, CancellationToken, Task<Result>> behaviour) =>
        _behaviours[receiver] = behaviour;

    /// <summary>Makes <paramref name="receiver"/> succeed.</summary>
    /// <param name="receiver">The receiver.</param>
    public void Succeed(string receiver) => Behave(receiver, (_, _) => Task.FromResult(Result.Success()));

    /// <summary>Makes <paramref name="receiver"/> return <see cref="FailedDelivery"/>.</summary>
    /// <param name="receiver">The receiver.</param>
    public void Fail(string receiver) => Behave(receiver, (_, _) => Task.FromResult(FailedDelivery()));

    /// <summary>Makes <paramref name="receiver"/> throw.</summary>
    /// <param name="receiver">The receiver.</param>
    public void Throw(string receiver) =>
        Behave(receiver, (_, _) => Task.FromException<Result>(new HttpRequestException("connection refused")));

    /// <summary>Counts one execution scope.</summary>
    public void CountExecutionScope() => Interlocked.Increment(ref _executionScopes);

    /// <summary>Records the invocation and runs the receiver's behaviour.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">The execution's token.</param>
    /// <returns>The behaviour's result.</returns>
    public async Task<Result> InvokeAsync(LaneProbeCommand command, CancellationToken cancellationToken)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        var observed = Volatile.Read(ref _maxInFlight);
        while (inFlight > observed)
        {
            var previous = Interlocked.CompareExchange(ref _maxInFlight, inFlight, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }

        try
        {
            _invocations.Enqueue(new LaneProbeInvocation(command.Receiver, command.Value, clock.GetUtcNow()));
            return _behaviours.TryGetValue(command.Receiver, out var behaviour)
                ? await behaviour(command, cancellationToken).ConfigureAwait(false)
                : Result.Success();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>The handler the test lane's rows execute through; it defers to <see cref="LaneProbeLog"/>.</summary>
/// <param name="log">The shared probe log.</param>
internal sealed class LaneProbeHandler(LaneProbeLog log) : ICommandHandler<LaneProbeCommand, Result>
{
    public Task<Result> HandleAsync(LaneProbeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return log.InvokeAsync(command, cancellationToken);
    }
}
