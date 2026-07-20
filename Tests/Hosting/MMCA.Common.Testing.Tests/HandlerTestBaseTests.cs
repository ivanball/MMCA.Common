using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Domain.Entities;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Exercises the <see cref="HandlerTestBase{THandler}"/> scaffold exactly the way a consumer
/// handler test class would: register repositories, then rely on the pre-wired unit of work.
/// </summary>
public sealed class HandlerTestBaseTests : HandlerTestBase<HandlerTestBaseTests.FakeHandler>
{
    [Fact]
    public void RegisterRepository_WiresMockInto_GetRepository_And_GetReadRepository()
    {
        var repository = RegisterRepository<TestAggregate, int>();

        UnitOfWork.Object.GetRepository<TestAggregate, int>().Should().BeSameAs(repository.Object);
        UnitOfWork.Object.GetReadRepository<TestAggregate, int>().Should().BeSameAs(repository.Object);
    }

    [Fact]
    public void RegisterReadRepository_WiresMockInto_GetReadRepository()
    {
        var repository = RegisterReadRepository<TestChildEntity, int>();

        UnitOfWork.Object.GetReadRepository<TestChildEntity, int>().Should().BeSameAs(repository.Object);
    }

    [Fact]
    public async Task SaveChangesAsync_DefaultsToSuccess()
    {
        var written = await UnitOfWork.Object.SaveChangesAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
    }

    [Fact]
    public void Logger_IsANullLoggerTypedToTheHandler() =>
        Logger.Should().BeSameAs(NullLogger<FakeHandler>.Instance);

    public sealed class FakeHandler;

    public sealed class TestAggregate : AuditableAggregateRootEntity<int>;

    public sealed class TestChildEntity : AuditableBaseEntity<int>;
}
