using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;

/// <summary>
/// Seeds an app-supplied list of development/test user accounts. The per-account idiom
/// (normalize the email, skip if it already exists, hash the password, build the aggregate, add,
/// save) was written out five times across the two app Identity modules; it lives here once.
/// </summary>
/// <remarks>
/// <para>
/// Two things stay app-specific and are reached only through hooks:
/// <list type="bullet">
///   <item><see cref="CreateUser"/> - the apps' <c>User.Create(...)</c> factories take the same
///     values in <b>different parameter orders</b>, and only the app can spell its own role
///     vocabulary, so the base never constructs the aggregate itself.</item>
///   <item><see cref="EmailExistsAsync"/> - the existence predicate is written against the app's
///     concrete <c>User</c> (never an interface member), so EF translation is byte-for-byte what it
///     was before the hoist. This mirrors <c>AuthenticationServiceBase&lt;TUser&gt;</c>.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="SeedAsync"/> seeds every account it is given: this type has no gate. Whether sample
/// accounts are seeded at all is the caller's decision, made in the app's <c>IModuleSeeder</c> before
/// it constructs and runs this seeder (typically on a configuration flag such as
/// <c>Seeding:IncludeSampleUsers</c>, default false, so a production host that sets nothing seeds no
/// accounts). Each account is saved individually, so one invalid account cannot roll back the others.
/// </para>
/// <para>
/// <strong>Concurrent replicas:</strong> the per-account idiom is check-then-insert, so two replicas
/// starting together on a database that lacks an account both pass the existence check and both
/// insert. The slower save is rejected by the unique index on the email. Each account is saved
/// through <see cref="DbSeeder.SaveToleratingConcurrentSeedAsync"/>, which reads that rejection as a
/// lost race (the winner wrote the very account this run meant to write) and discards the failed
/// save; the skip is logged and seeding continues with the next account. Every other save failure
/// still propagates.
/// </para>
/// <para>
/// <strong>Security notice:</strong> seed credentials are deliberately weak plaintext values for
/// local development convenience. Deployed environments must disable seeding or supply
/// environment-sourced secrets.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <param name="unitOfWork">The unit of work the accounts are written through.</param>
/// <param name="passwordHasher">The hasher applied to each account's plaintext seed password.</param>
/// <param name="logger">Receives one line per account lost to a concurrent replica's seed.</param>
public abstract partial class IdentityModuleDbSeederBase<TUser>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILogger logger) : DbSeeder
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityModuleDbSeederBase{TUser}"/> class
    /// that logs nothing.
    /// </summary>
    /// <param name="unitOfWork">The unit of work the accounts are written through.</param>
    /// <param name="passwordHasher">The hasher applied to each account's plaintext seed password.</param>
    protected IdentityModuleDbSeederBase(IUnitOfWork unitOfWork, IPasswordHasher passwordHasher)
        : this(unitOfWork, passwordHasher, NullLogger.Instance)
    {
    }

    /// <summary>The unit of work the accounts are written through.</summary>
    protected IUnitOfWork UnitOfWork { get; } = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    /// <summary>The hasher applied to each account's plaintext seed password (ADR-032).</summary>
    protected IPasswordHasher PasswordHasher { get; } = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));

    /// <summary>The accounts to seed, in order.</summary>
    protected abstract IReadOnlyList<SeedAccount> Accounts { get; }

    /// <inheritdoc />
    public override async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var account in Accounts)
        {
            await SeedAccountAsync(account, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether an account with this email already exists. Implement with a predicate on the app's
    /// concrete <c>User</c> (e.g. <c>u =&gt; u.Email == email</c>).
    /// </summary>
    /// <param name="email">The normalized email value object; <see langword="null"/> when the seed
    /// address failed validation, in which case no user can match it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the account is already seeded.</returns>
    protected abstract Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the app's <c>User</c> from a seed account via its own domain factory.
    /// </summary>
    /// <param name="account">The account being seeded.</param>
    /// <param name="passwordHash">The hash of <see cref="SeedAccount.Password"/>.</param>
    /// <param name="passwordSalt">The salt paired with <paramref name="passwordHash"/>.</param>
    /// <returns>The created aggregate, or a failure (which skips this account silently, as before).</returns>
    protected abstract Result<TUser> CreateUser(SeedAccount account, byte[] passwordHash, byte[] passwordSalt);

    private async Task SeedAccountAsync(SeedAccount account, CancellationToken cancellationToken)
    {
        // Normalize to the Email value object so the EF predicate compares same-typed converted values.
        var email = Email.Create(account.Email).Value;

        var exists = await EmailExistsAsync(email, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        var (hash, salt) = PasswordHasher.HashPassword(account.Password);
        var userResult = CreateUser(account, hash, salt);
        if (!userResult.TryGetValue(out var user))
        {
            return;
        }

        var repository = UnitOfWork.GetRepository<TUser, UserIdentifierType>();
        await repository.AddAsync(user, cancellationToken).ConfigureAwait(false);

        var saved = await SaveToleratingConcurrentSeedAsync(UnitOfWork, cancellationToken).ConfigureAwait(false);
        if (!saved)
        {
            // Lost the insert race to a concurrent replica seeding the same account (see the type
            // remarks): its row is the one this run meant to write, so move on to the next account.
            LogSeedAccountAlreadySeededConcurrently(_logger, typeof(TUser).Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "A seed {UserType} account was inserted concurrently by another instance; skipping it and continuing with the next account")]
    private static partial void LogSeedAccountAlreadySeededConcurrently(ILogger logger, string userType);
}
