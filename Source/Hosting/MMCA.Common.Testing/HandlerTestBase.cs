using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Testing;

/// <summary>
/// Reusable Moq scaffold for command/query handler unit tests — the shared replacement for the
/// per-test copy-paste of <c>Mock&lt;IUnitOfWork&gt;</c> + <c>GetRepository</c> wiring +
/// <c>SaveChangesAsync</c> setup that otherwise repeats across every handler test class.
/// <para>
/// Derive per handler test class, call <see cref="RegisterRepository{TEntity, TIdentifierType}"/>
/// (or <see cref="RegisterReadRepository{TEntity, TIdentifierType}"/>) for each aggregate the
/// handler touches, then construct the handler with <see cref="UnitOfWork"/>.<c>Object</c> and
/// <see cref="Logger"/>:
/// </para>
/// <code>
/// public sealed class CreateEventHandlerTests : HandlerTestBase&lt;CreateEventHandler&gt;
/// {
///     private readonly Mock&lt;IRepository&lt;Event, EventIdentifierType&gt;&gt; _events;
///     private readonly CreateEventHandler _sut;
///
///     public CreateEventHandlerTests()
///     {
///         _events = RegisterRepository&lt;Event, EventIdentifierType&gt;();
///         _sut = new CreateEventHandler(UnitOfWork.Object, ..., Logger);
///     }
/// }
/// </code>
/// <para>
/// <see cref="UnitOfWork"/> arrives with <c>SaveChangesAsync</c> pre-configured to succeed
/// (returning 1 written entry); override with your own <c>Setup</c> for failure-path tests.
/// </para>
/// </summary>
/// <typeparam name="THandler">The handler under test (also types <see cref="Logger"/>).</typeparam>
public abstract class HandlerTestBase<THandler>
    where THandler : class
{
    protected HandlerTestBase() =>
        UnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

    /// <summary>The unit-of-work mock every registered repository is wired into.</summary>
    protected Mock<IUnitOfWork> UnitOfWork { get; } = new();

    /// <summary>A no-op logger typed to the handler under test.</summary>
    protected ILogger<THandler> Logger { get; } = NullLogger<THandler>.Instance;

    /// <summary>
    /// Creates a read-write repository mock and wires it into
    /// <see cref="IUnitOfWork.GetRepository{TEntity, TIdentifierType}"/> and
    /// <see cref="IUnitOfWork.GetReadRepository{TEntity, TIdentifierType}"/>.
    /// </summary>
    /// <returns>The repository mock, for further <c>Setup</c>/<c>Verify</c> calls.</returns>
    protected Mock<IRepository<TEntity, TIdentifierType>> RegisterRepository<TEntity, TIdentifierType>()
        where TEntity : AuditableAggregateRootEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var repository = new Mock<IRepository<TEntity, TIdentifierType>>();
        UnitOfWork.Setup(u => u.GetRepository<TEntity, TIdentifierType>()).Returns(repository.Object);
        UnitOfWork.Setup(u => u.GetReadRepository<TEntity, TIdentifierType>()).Returns(repository.Object);
        return repository;
    }

    /// <summary>
    /// Creates a read-only repository mock and wires it into
    /// <see cref="IUnitOfWork.GetReadRepository{TEntity, TIdentifierType}"/> — for non-aggregate
    /// entities (child entities) that expose no read-write repository.
    /// </summary>
    /// <returns>The repository mock, for further <c>Setup</c>/<c>Verify</c> calls.</returns>
    protected Mock<IReadRepository<TEntity, TIdentifierType>> RegisterReadRepository<TEntity, TIdentifierType>()
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var repository = new Mock<IReadRepository<TEntity, TIdentifierType>>();
        UnitOfWork.Setup(u => u.GetReadRepository<TEntity, TIdentifierType>()).Returns(repository.Object);
        return repository;
    }
}
