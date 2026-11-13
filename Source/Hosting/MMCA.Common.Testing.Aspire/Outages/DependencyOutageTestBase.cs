using System.Diagnostics;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using MMCA.Common.Testing.Aspire.Fixtures;
using MMCA.Common.Testing.Aspire.Probes;

namespace MMCA.Common.Testing.Aspire.Outages;

/// <summary>
/// Base class for AppHost tests that take a dependency away from a running stack and assert how a
/// service behaves while it is gone and after it is back: readiness follows a database and never a
/// broker or a cache, liveness follows nothing, the full health report follows everything, and an
/// outbox that parked integration events during a broker outage drains once the broker returns.
/// <para>
/// <see cref="RunOutageAsync"/> runs one scenario end to end. It opens with a baseline (the service
/// must be ready and alive, so a previous scenario must have recovered), stops the dependency through
/// the orchestrator's stop command, asserts the outage answers, runs the scenario's own work, starts
/// the dependency again and asserts recovery. If anything fails while the dependency is down, the
/// start command is still issued once, so one failed scenario does not take the next one down with it.
/// </para>
/// <para>
/// <b>Data survival.</b> A database outage only recovers when the stopped resource keeps its files
/// across a stop and start (a data volume or a persistent lifetime) or the service rebuilds its own
/// schema, because Aspire recreates the container on start.
/// </para>
/// <para>
/// No wait in this base gates on readiness; readiness is only ever asserted. Skipping stays the
/// subclass's call: check <c>Fixture.IsAvailable</c> and skip with <c>Fixture.SkipReason</c>.
/// </para>
/// </summary>
/// <typeparam name="TFixture">The collection fixture that owns the running application.</typeparam>
/// <param name="fixture">The collection fixture.</param>
public abstract class DependencyOutageTestBase<TFixture>(TFixture fixture) : AppHostTestBase<TFixture>(fixture)
    where TFixture : AppHostFixtureBase
{
    /// <summary>
    /// Counts the pending outbox rows of a table given as <c>schema.table</c> (or a bare table in
    /// <c>dbo</c>). The identifiers are quoted server-side, so the command text stays constant.
    /// </summary>
    private const string CountPendingOutboxSql =
        "DECLARE @schema sysname = COALESCE(PARSENAME(@table, 2), N'dbo'); "
        + "DECLARE @name sysname = PARSENAME(@table, 1); "
        + "DECLARE @sql nvarchar(max) = N'SELECT @count = COUNT_BIG(*) FROM ' "
        + "+ QUOTENAME(@schema) + N'.' + QUOTENAME(@name) + N' WHERE ProcessedOn IS NULL'; "
        + "DECLARE @count bigint; "
        + "EXEC sp_executesql @sql, N'@count bigint OUTPUT', @count = @count OUTPUT; "
        + "SELECT @count;";

    /// <summary>The longest a single probe request may take before it counts as "not yet".</summary>
    private static readonly TimeSpan ProbeRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The time budgets the scenario spends. Defaults to <see cref="DependencyOutageBudget.Default"/>.</summary>
    protected virtual DependencyOutageBudget OutageBudget => DependencyOutageBudget.Default;

    /// <summary>The delay between two probe requests while polling. Defaults to one second.</summary>
    protected virtual TimeSpan ProbeInterval => TimeSpan.FromSeconds(1);

    /// <summary>
    /// The outbox table <see cref="CountPendingOutboxMessagesAsync"/> counts. Defaults to
    /// <c>dbo.OutboxMessages</c>, where the framework maps the outbox on SQL Server.
    /// </summary>
    protected virtual string OutboxTableName => "dbo.OutboxMessages";

    /// <summary>
    /// Runs one outage scenario end to end: baseline, stop, outage answers, the scenario's own work
    /// (and a pending-outbox check when asked), start, recovery, and a drained outbox when asked.
    /// <para>
    /// A database outage only recovers when the stopped resource keeps its files across a stop and
    /// start (a data volume or a persistent lifetime) or the service rebuilds its own schema, because
    /// Aspire recreates the container on start.
    /// </para>
    /// </summary>
    /// <param name="scenario">The scenario to run.</param>
    /// <param name="cancellationToken">Cancels the scenario.</param>
    /// <returns>A task that completes when every assertion held.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A budget in <see cref="OutageBudget"/> is zero or negative.</exception>
    /// <exception cref="ArgumentException">
    /// The scenario asks for an outbox check on an outage that is not a broker outage.
    /// </exception>
    protected async Task RunOutageAsync(DependencyOutageScenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var budget = OutageBudget;
        budget.Validate();

        if (scenario.OutboxDatabaseResourceName is not null && scenario.Kind != DependencyOutageKind.Broker)
        {
            throw new ArgumentException(
                $"OutboxDatabaseResourceName is only valid on a {nameof(DependencyOutageKind.Broker)} outage "
                + $"(this scenario is {scenario.Kind}): a stopped database cannot be counted, and a cache outage "
                + "parks no outbox row.",
                nameof(scenario));
        }

        var expectation = OutageExpectation.For(scenario.Kind);
        var service = scenario.ServiceResourceName;
        var endpoint = scenario.ServiceEndpointName;

        // Baseline: a previous scenario must have recovered before this one takes anything away.
        await AssertProbeEventuallyAsync(service, AppHostProbePaths.Ready, HttpStatusCode.OK, endpoint, cancellationToken)
            .ConfigureAwait(false);
        await AssertProbeEventuallyAsync(service, AppHostProbePaths.Alive, HttpStatusCode.OK, endpoint, cancellationToken)
            .ConfigureAwait(false);

        var started = false;
        try
        {
            await StopResourceAsync(scenario.DependencyResourceName, cancellationToken).ConfigureAwait(false);

            // The full report first: for a broker or a cache, readiness is then observed while the
            // service is already known to be degraded.
            await AssertProbeEventuallyAsync(service, AppHostProbePaths.Health, expectation.Health, endpoint, cancellationToken)
                .ConfigureAwait(false);
            await AssertProbeEventuallyAsync(service, AppHostProbePaths.Ready, expectation.Ready, endpoint, cancellationToken)
                .ConfigureAwait(false);

            // Liveness never follows a dependency.
            var alive = await GetProbeStatusAsync(service, AppHostProbePaths.Alive, endpoint, cancellationToken)
                .ConfigureAwait(false);
            alive.Should().Be(
                HttpStatusCode.OK,
                "'{0}' must stay alive while '{1}' is down; liveness never follows a dependency",
                service,
                scenario.DependencyResourceName);

            if (scenario.DuringOutage is not null)
            {
                await scenario.DuringOutage(cancellationToken).ConfigureAwait(false);
            }

            if (scenario.OutboxDatabaseResourceName is not null)
            {
                var pending = await CountPendingOutboxMessagesAsync(scenario.OutboxDatabaseResourceName, cancellationToken)
                    .ConfigureAwait(false);
                pending.Should().BeGreaterThan(
                    0,
                    "an integration event written while '{0}' is down must wait in the outbox of '{1}'",
                    scenario.DependencyResourceName,
                    scenario.OutboxDatabaseResourceName);
            }

            await StartResourceAsync(scenario.DependencyResourceName, cancellationToken).ConfigureAwait(false);
            started = true;
        }
        finally
        {
            if (!started)
            {
                await TryIssueStartCommandAsync(scenario.DependencyResourceName).ConfigureAwait(false);
            }
        }

        // Recovery.
        await AssertProbeEventuallyAsync(service, AppHostProbePaths.Health, HttpStatusCode.OK, endpoint, cancellationToken)
            .ConfigureAwait(false);
        await AssertProbeEventuallyAsync(service, AppHostProbePaths.Ready, HttpStatusCode.OK, endpoint, cancellationToken)
            .ConfigureAwait(false);

        if (scenario.OutboxDatabaseResourceName is not null)
        {
            await AssertOutboxDrainedAsync(scenario.OutboxDatabaseResourceName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops a resource through the orchestrator's stop command and waits, within
    /// <see cref="DependencyOutageBudget.StateChange"/>, for it to report <c>Exited</c> or <c>Finished</c>.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="cancellationToken">Cancels the stop.</param>
    /// <returns>A task that completes when the resource reports a stopped state.</returns>
    /// <exception cref="InvalidOperationException">The stop command failed.</exception>
    /// <exception cref="TimeoutException">The resource did not report a stopped state in time.</exception>
    protected async Task StopResourceAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        await ExecuteCommandAsync(resourceName, KnownResourceCommands.StopCommand, cancellationToken).ConfigureAwait(false);

        var budget = OutageBudget.StateChange;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(budget);

        try
        {
            await Application.ResourceNotifications
                .WaitForResourceAsync(resourceName, [KnownResourceStates.Exited, KnownResourceStates.Finished], wait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Resource '{resourceName}' did not report {KnownResourceStates.Exited} or {KnownResourceStates.Finished} "
                + $"within {budget} of its stop command.",
                exception);
        }
    }

    /// <summary>
    /// Starts a resource through the orchestrator's start command and waits, within
    /// <see cref="DependencyOutageBudget.StateChange"/>, for it to report healthy.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes when the resource reports healthy.</returns>
    /// <exception cref="InvalidOperationException">The start command failed.</exception>
    /// <exception cref="TimeoutException">The resource did not report healthy in time.</exception>
    protected async Task StartResourceAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        await ExecuteCommandAsync(resourceName, KnownResourceCommands.StartCommand, cancellationToken).ConfigureAwait(false);

        var budget = OutageBudget.StateChange;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(budget);

        try
        {
            await Application.ResourceNotifications
                .WaitForResourceHealthyAsync(resourceName, wait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Resource '{resourceName}' did not report healthy within {budget} of its start command.",
                exception);
        }
    }

    /// <summary>
    /// GETs one path on a resource and returns the status code, or <see langword="null"/> when the
    /// request throws <see cref="HttpRequestException"/> or times out (which counts as "not yet",
    /// never as a pass).
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="path">The path to GET.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status code, or <see langword="null"/> when no answer arrived.</returns>
    protected async Task<HttpStatusCode?> GetProbeStatusAsync(
        string resourceName,
        string path,
        string? endpointName = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateHttpClient(resourceName, endpointName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeRequestTimeout);

        try
        {
            using var response = await client
                .GetAsync(new Uri(path, UriKind.Relative), timeout.Token)
                .ConfigureAwait(false);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Polls one path on a resource every <see cref="ProbeInterval"/> until it answers
    /// <paramref name="expected"/>, and fails with the last answer when
    /// <see cref="DependencyOutageBudget.Probe"/> runs out.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="path">The path to GET.</param>
    /// <param name="expected">The status code the path must reach.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the polling.</param>
    /// <returns>A task that completes when the path answered <paramref name="expected"/>.</returns>
    protected async Task AssertProbeEventuallyAsync(
        string resourceName,
        string path,
        HttpStatusCode expected,
        string? endpointName = null,
        CancellationToken cancellationToken = default)
    {
        var budget = OutageBudget.Probe;
        var elapsed = Stopwatch.StartNew();

        var last = await GetProbeStatusAsync(resourceName, path, endpointName, cancellationToken).ConfigureAwait(false);
        while (last != expected && elapsed.Elapsed < budget)
        {
            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
            last = await GetProbeStatusAsync(resourceName, path, endpointName, cancellationToken).ConfigureAwait(false);
        }

        last.Should().Be(
            expected,
            "'{0}' must answer {1} with {2} within {3} (last answer: {4})",
            resourceName,
            path,
            (int)expected,
            budget,
            last is null ? "no answer" : ((int)last.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Counts the outbox rows not yet processed in a SQL Server database resource: resolves the
    /// resource's connection string and counts, through <c>Microsoft.Data.SqlClient</c>, the rows of
    /// <see cref="OutboxTableName"/> whose <c>ProcessedOn</c> is null (<c>COUNT_BIG(*)</c>). Override it
    /// for another engine.
    /// </summary>
    /// <param name="databaseResourceName">The database resource that holds the outbox.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The number of pending outbox rows.</returns>
    /// <exception cref="InvalidOperationException">The resource resolves no connection string.</exception>
    protected virtual async Task<long> CountPendingOutboxMessagesAsync(
        string databaseResourceName,
        CancellationToken cancellationToken = default)
    {
        var connectionString = await GetConnectionStringAsync(databaseResourceName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Database resource '{databaseResourceName}' resolves no connection string, so its outbox cannot be counted.");

        var connection = new SqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = new SqlCommand(CountPendingOutboxSql, connection);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 256) { Value = OutboxTableName });

                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return result is null or DBNull ? 0L : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Polls <see cref="CountPendingOutboxMessagesAsync"/> every <see cref="ProbeInterval"/> until it
    /// reaches zero, and fails with the last count when <see cref="DependencyOutageBudget.Drain"/> runs out.
    /// </summary>
    /// <param name="databaseResourceName">The database resource that holds the outbox.</param>
    /// <param name="cancellationToken">Cancels the polling.</param>
    /// <returns>A task that completes when the outbox is drained.</returns>
    protected async Task AssertOutboxDrainedAsync(string databaseResourceName, CancellationToken cancellationToken = default)
    {
        var budget = OutageBudget.Drain;
        var elapsed = Stopwatch.StartNew();

        var pending = await CountPendingOutboxMessagesAsync(databaseResourceName, cancellationToken).ConfigureAwait(false);
        while (pending > 0 && elapsed.Elapsed < budget)
        {
            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
            pending = await CountPendingOutboxMessagesAsync(databaseResourceName, cancellationToken).ConfigureAwait(false);
        }

        pending.Should().Be(
            0,
            "the outbox of '{0}' must drain within {1} once its broker is back (last count: {2})",
            databaseResourceName,
            budget,
            pending);
    }

    /// <summary>Executes one resource command and throws when it reports failure.</summary>
    private async Task ExecuteCommandAsync(string resourceName, string commandName, CancellationToken cancellationToken)
    {
        var result = await Application.ResourceCommands
            .ExecuteCommandAsync(resourceName, commandName, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"The '{commandName}' command on resource '{resourceName}' failed: {result.Message ?? "no message"}.");
        }
    }

    /// <summary>
    /// Issues the start command once after a failure while the dependency was down, so the next
    /// scenario does not inherit the outage. Best effort and bounded by
    /// <see cref="DependencyOutageBudget.StateChange"/>: whatever it throws is swallowed and a start
    /// that does not finish in time is abandoned, so the original failure is the one reported.
    /// </summary>
    private Task TryIssueStartCommandAsync(string resourceName) =>
        OutageCommands.RunBestEffortAsync(
            token => Application.ResourceCommands.ExecuteCommandAsync(resourceName, KnownResourceCommands.StartCommand, token),
            OutageBudget.StateChange);
}
