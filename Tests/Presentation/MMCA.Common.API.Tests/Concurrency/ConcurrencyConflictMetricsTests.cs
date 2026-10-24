using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Http;
using Moq;

namespace MMCA.Common.API.Tests.Concurrency;

/// <summary>
/// The <c>persistence.concurrency.conflict</c> counter: one increment, tagged with the entity type,
/// for every optimistic-concurrency conflict the API answers, whether the global
/// <see cref="DbUpdateExceptionHandler"/> (409) or <see cref="SupportsIfMatchAttribute"/> (412)
/// answers it, and nothing for any other database update failure. The exceptions are real ones,
/// raised by EF Core against an in-memory SQLite database, so <c>Entries</c> is what a host sees.
/// Measurements are filtered by this file's probe entity types, because the meter is a
/// process-wide static shared with tests running in parallel.
/// </summary>
public sealed class ConcurrencyConflictMetricsTests
{
    // Duplicated literals: the metrics class is internal (the same duplication the Aspire package
    // makes). A rename fails this test, which is the point: exporters subscribe by these names.
    private const string MeterName = "MMCA.Common.Persistence";
    private const string InstrumentName = "persistence.concurrency.conflict";
    private const string EntityTypeTag = "entity_type";

    [Fact]
    public async Task Handler_ConcurrencyException_IncrementsTheCounterByExactlyOneWithTheEntityType()
    {
        var exception = await CaptureSaveFailureAsync<HandlerConflictProbe>(ConflictKind.Concurrency);
        exception.Should().BeOfType<DbUpdateConcurrencyException>();

        var measurements = await CaptureAsync(nameof(HandlerConflictProbe), async () =>
        {
            var handled = await CreateHandler().TryHandleAsync(new DefaultHttpContext(), exception, CancellationToken.None);
            handled.Should().BeTrue();
        });

        measurements.Should().ContainSingle("one conflict is one increment").Which.Should().Be(1);
    }

    [Fact]
    public async Task Handler_OtherDbUpdateException_DoesNotIncrementTheCounter()
    {
        var exception = await CaptureSaveFailureAsync<HandlerUniqueKeyProbe>(ConflictKind.DuplicateKey);
        exception.Should().NotBeAssignableTo<DbUpdateConcurrencyException>("the probe must be a different failure");
        exception.Entries.Should().NotBeEmpty("the exception names the entity, so a wrong increment would be visible");

        var measurements = await CaptureAsync(nameof(HandlerUniqueKeyProbe), async () =>
        {
            var handled = await CreateHandler().TryHandleAsync(new DefaultHttpContext(), exception, CancellationToken.None);
            handled.Should().BeTrue("the handler still answers it as a 409");
        });

        measurements.Should().BeEmpty("a unique-key failure is not a concurrency conflict");
    }

    [Fact]
    public async Task IfMatchFilter_ConcurrencyException_IncrementsTheCounterByExactlyOneWithTheEntityType()
    {
        var exception = (DbUpdateConcurrencyException)await CaptureSaveFailureAsync<IfMatchConflictProbe>(ConflictKind.Concurrency);

        var measurements = await CaptureAsync(nameof(IfMatchConflictProbe), async () =>
        {
            var executed = await RunIfMatchFilterAsync(exception);
            executed.ExceptionHandled.Should().BeTrue("the filter answers it, so the global handler never counts it again");
        });

        measurements.Should().ContainSingle("one conflict is one increment").Which.Should().Be(1);
    }

    private static DbUpdateExceptionHandler CreateHandler()
    {
        var problemDetails = new Mock<IProblemDetailsService>();
        problemDetails.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>())).ReturnsAsync(true);
        return new DbUpdateExceptionHandler(problemDetails.Object, NullLogger<DbUpdateExceptionHandler>.Instance);
    }

    private static async Task<ActionExecutedContext> RunIfMatchFilterAsync(Exception actionException)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "PUT";
        httpContext.Request.Headers[ConcurrencyETag.IfMatchHeaderName] = ConcurrencyETag.Format([0, 0, 0, 0, 0, 0, 7, 209]);
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(StringComparer.Ordinal), null!);

        ActionExecutedContext? executed = null;
        await new SupportsIfMatchAttribute().OnActionExecutionAsync(context, () =>
        {
            executed = new ActionExecutedContext(context, [], null!) { Exception = actionException };
            return Task.FromResult(executed);
        });

        return executed!;
    }

    /// <summary>
    /// Raises a real save failure for <typeparamref name="TEntity"/>: an update of a row that does not
    /// exist (EF reports it as a concurrency conflict) or an insert of a duplicate primary key.
    /// </summary>
    private static async Task<DbUpdateException> CaptureSaveFailureAsync<TEntity>(ConflictKind kind)
        where TEntity : ConflictProbe, new()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new ProbeContext<TEntity>(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        if (kind == ConflictKind.DuplicateKey)
        {
            context.Add(new TEntity { Id = 1 });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.ChangeTracker.Clear();
            context.Add(new TEntity { Id = 1 });
        }
        else
        {
            context.Update(new TEntity { Id = 42 });
        }

        try
        {
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        catch (DbUpdateException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The probe save was expected to fail.");
    }

    private static async Task<List<long>> CaptureAsync(string entityType, Func<Task> act)
    {
        var gate = new System.Threading.Lock();
        var captured = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, InstrumentName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, EntityTypeTag, StringComparison.Ordinal)
                    && Equals(tag.Value, entityType))
                {
                    lock (gate)
                    {
                        captured.Add(value);
                    }
                }
            }
        });
        listener.Start();

        await act();

        lock (gate)
        {
            return [.. captured];
        }
    }

    private enum ConflictKind
    {
        Concurrency,
        DuplicateKey,
    }

    /// <summary>Base shape of the probe entities; one subclass per test keeps their tags apart.</summary>
    public abstract class ConflictProbe
    {
        /// <summary>Gets or sets the key.</summary>
        public int Id { get; set; }

        /// <summary>Gets or sets a non-key column, so an update of the row has something to write.</summary>
        public string Name { get; set; } = "probe";
    }

    /// <summary>Probe for the handler's concurrency path.</summary>
    public sealed class HandlerConflictProbe : ConflictProbe;

    /// <summary>Probe for the handler's non-concurrency path.</summary>
    public sealed class HandlerUniqueKeyProbe : ConflictProbe;

    /// <summary>Probe for the If-Match filter's concurrency path.</summary>
    public sealed class IfMatchConflictProbe : ConflictProbe;

    private sealed class ProbeContext<TEntity>(SqliteConnection connection) : DbContext
        where TEntity : ConflictProbe
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlite(connection);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<ConflictProbe>();
            modelBuilder.Entity<TEntity>().Property(e => e.Id).ValueGeneratedNever();
        }
    }
}
