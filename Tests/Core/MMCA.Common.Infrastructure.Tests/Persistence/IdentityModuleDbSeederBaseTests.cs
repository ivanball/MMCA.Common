using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the shared Identity seeding loop: the opt-in gate, the per-account
/// exists-then-hash-then-create-then-save idiom, the per-account save boundary, and the delegation of
/// aggregate construction to the app (the two apps' <c>User.Create</c> factories take their arguments
/// in different orders).
/// </summary>
public sealed class IdentityModuleDbSeederBaseTests
{
    private static readonly SeedAccount[] TwoAccounts =
    [
        new("admin@example.com", "Admin123!", "Admin", "Admin", "User"),
        new("customer@example.com", "Password", "Customer", "Ivan", "Ball"),
    ];

    [Fact]
    public async Task SeedAsync_WhenGateIsClosed_SeedsNothing()
    {
        var (sut, mocks) = CreateSut(TwoAccounts, shouldSeed: false);

        await sut.SeedAsync(CancellationToken.None);

        sut.CreateUserCalls.Should().Be(0);
        mocks.Repository.Verify(
            x => x.AddAsync(It.IsAny<TestSeedUser>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SeedAsync_WhenGateIsOpen_AddsAndSavesEachAccountIndividually()
    {
        var (sut, mocks) = CreateSut(TwoAccounts);

        await sut.SeedAsync(CancellationToken.None);

        sut.CreateUserCalls.Should().Be(2);
        mocks.Repository.Verify(
            x => x.AddAsync(It.IsAny<TestSeedUser>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        mocks.UnitOfWork.Verify(
            x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "each account is saved on its own so one bad account cannot roll back the others");
    }

    [Fact]
    public async Task SeedAsync_NormalizesTheEmailBeforeTheExistenceCheck()
    {
        var (sut, _) = CreateSut([TwoAccounts[0]]);

        await sut.SeedAsync(CancellationToken.None);

        sut.CheckedEmails.Should().ContainSingle();
        sut.CheckedEmails[0]!.Value.Should().Be("admin@example.com");
    }

    [Fact]
    public async Task SeedAsync_WhenAccountAlreadyExists_SkipsItWithoutHashingOrSaving()
    {
        var (sut, mocks) = CreateSut(TwoAccounts);
        sut.ExistingEmails.Add("admin@example.com");

        await sut.SeedAsync(CancellationToken.None);

        sut.CreateUserCalls.Should().Be(1);
        mocks.PasswordHasher.Verify(x => x.HashPassword("Admin123!"), Times.Never);
        mocks.PasswordHasher.Verify(x => x.HashPassword("Password"), Times.Once);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SeedAsync_WhenTheFactoryRejectsAnAccount_SkipsItAndContinues()
    {
        var (sut, mocks) = CreateSut(TwoAccounts);
        sut.RejectedEmails.Add("admin@example.com");

        await sut.SeedAsync(CancellationToken.None);

        mocks.Repository.Verify(
            x => x.AddAsync(It.IsAny<TestSeedUser>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SeedAsync_HandsTheHashedCredentialToTheAppFactory()
    {
        var (sut, _) = CreateSut([TwoAccounts[0]]);

        await sut.SeedAsync(CancellationToken.None);

        sut.CreatedUsers.Should().ContainSingle();
        sut.CreatedUsers[0].PasswordHash.Should().BeEquivalentTo(new byte[] { 1, 1, 1 });
        sut.CreatedUsers[0].PasswordSalt.Should().BeEquivalentTo(new byte[] { 2, 2, 2 });
        sut.CreatedUsers[0].Role.Should().Be("Admin");
    }

    private sealed record SeederMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IRepository<TestSeedUser, UserIdentifierType>> Repository,
        Mock<IPasswordHasher> PasswordHasher);

    private static (TestIdentityModuleDbSeeder Sut, SeederMocks Mocks) CreateSut(
        IReadOnlyList<SeedAccount> accounts,
        bool shouldSeed = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IRepository<TestSeedUser, UserIdentifierType>>();
        var passwordHasher = new Mock<IPasswordHasher>();

        unitOfWork.Setup(x => x.GetRepository<TestSeedUser, UserIdentifierType>()).Returns(repository.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        passwordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns((new byte[] { 1, 1, 1 }, new byte[] { 2, 2, 2 }));

        var sut = new TestIdentityModuleDbSeeder(unitOfWork.Object, passwordHasher.Object, accounts, shouldSeed);
        return (sut, new SeederMocks(unitOfWork, repository, passwordHasher));
    }
}

/// <summary>Minimal <c>User</c> aggregate for the seeding tests (public so Moq can proxy the repository).</summary>
public sealed class TestSeedUser : AuditableAggregateRootEntity<UserIdentifierType>
{
    public required string Role { get; init; }

#pragma warning disable CA1819 // Properties should not return arrays: mirrors IAuthUser's byte[] credential material.
    public required byte[] PasswordHash { get; init; }

    public required byte[] PasswordSalt { get; init; }
#pragma warning restore CA1819
}

/// <summary>Concrete subclass standing in for an app's <c>IdentityModuleDbSeeder</c>.</summary>
public sealed class TestIdentityModuleDbSeeder(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IReadOnlyList<SeedAccount> accounts,
    bool shouldSeed) : IdentityModuleDbSeederBase<TestSeedUser>(unitOfWork, passwordHasher)
{
    public HashSet<string> ExistingEmails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> RejectedEmails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Email?> CheckedEmails { get; } = [];

    public List<TestSeedUser> CreatedUsers { get; } = [];

    public int CreateUserCalls { get; private set; }

    protected override IReadOnlyList<SeedAccount> Accounts => accounts;

    protected override bool ShouldSeed => shouldSeed;

    protected override Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken)
    {
        CheckedEmails.Add(email);
        return Task.FromResult(email is not null && ExistingEmails.Contains(email.Value));
    }

    protected override Result<TestSeedUser> CreateUser(SeedAccount account, byte[] passwordHash, byte[] passwordSalt)
    {
        ArgumentNullException.ThrowIfNull(account);

        CreateUserCalls++;
        if (RejectedEmails.Contains(account.Email))
        {
            return Result.Failure<TestSeedUser>(Error.Invariant("User.Invalid", "Seed account rejected."));
        }

        var user = new TestSeedUser
        {
            Id = CreateUserCalls,
            Role = account.Role,
            PasswordHash = passwordHash,
            PasswordSalt = passwordSalt,
        };
        CreatedUsers.Add(user);
        return Result.Success(user);
    }
}
