using Microsoft.EntityFrameworkCore;
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
/// <see cref="ShouldSeed"/> is the opt-in gate: it defaults to <see langword="true"/> (seed
/// unconditionally, Store's behavior), and an app that gates its sample accounts on configuration
/// (ADC's <c>Seeding:IncludeSampleUsers</c>, default false) overrides it. Each account is saved
/// individually, exactly as before, so one invalid account cannot roll back the others.
/// </para>
/// <para>
/// <strong>Concurrent replicas:</strong> the per-account idiom is check-then-insert, so two replicas
/// starting together on a database that lacks an account both pass the existence check and both
/// insert. The slower save is rejected by the unique index on the email. That rejection is a lost
/// race, not a fault: the winner wrote the very account this run meant to write. A unique or
/// primary-key violation on an account's save is therefore logged, the failed save's pending
/// entries are detached (so the next account's save does not replay the rejected insert), and
/// seeding continues with the next account. Every other save failure still propagates. The
/// violation is classified by the unit of work's registered
/// <see cref="IUniqueConstraintViolationDetector"/> when it exposes one (the host's engine-aware
/// choice), else by <see cref="SqlServerUniqueConstraintViolationDetector"/>.
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

    /// <summary>
    /// Whether to seed at all (default: yes). Override to reproduce a configuration gate such as
    /// ADC's <c>Seeding:IncludeSampleUsers</c>, which defaults to false so a production host that
    /// sets nothing seeds no accounts.
    /// </summary>
    protected virtual bool ShouldSeed => true;

    /// <inheritdoc />
    public override async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!ShouldSeed)
        {
            return;
        }

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
        if (userResult.IsFailure)
        {
            return;
        }

        var repository = UnitOfWork.GetRepository<TUser, UserIdentifierType>();
        await repository.AddAsync(userResult.Value!, cancellationToken).ConfigureAwait(false);

        try
        {
            await UnitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUniqueConstraintViolation(exception))
        {
            // Lost the insert race to a concurrent replica seeding the same account (see the type
            // remarks): its row is the one this run meant to write, so move on to the next account.
            DiscardFailedSave(exception);
            LogSeedAccountAlreadySeededConcurrently(_logger, typeof(TUser).Name);
        }
    }

    // The fallback is built on demand rather than cached in a static: this runs only on a failed
    // save, and a static in a generic type would be one instance per closed TUser anyway.
    private bool IsUniqueConstraintViolation(Exception exception) =>
        (UnitOfWork as IUniqueConstraintViolationDetector ?? new SqlServerUniqueConstraintViolationDetector())
            .IsUniqueConstraintViolation(exception);

    /// <summary>
    /// Detaches every pending entry of each context the failed save touched. A failed save rolls
    /// back as a whole, so everything still pending there belongs to it (the rejected account and
    /// any rows captured with it); left tracked, the scoped context would replay the same rejected
    /// insert on the next account's save and fail again.
    /// </summary>
    private static void DiscardFailedSave(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not DbUpdateException updateException)
            {
                continue;
            }

            foreach (var context in updateException.Entries.Select(entry => entry.Context).Distinct())
            {
                var pending = context.ChangeTracker.Entries()
                    .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    .ToList();

                foreach (var entry in pending)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "A seed {UserType} account was inserted concurrently by another instance; skipping it and continuing with the next account")]
    private static partial void LogSeedAccountAlreadySeededConcurrently(ILogger logger, string userType);
}
