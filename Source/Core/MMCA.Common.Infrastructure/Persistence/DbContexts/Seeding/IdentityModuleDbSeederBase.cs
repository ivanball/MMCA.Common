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
/// <strong>Security notice:</strong> seed credentials are deliberately weak plaintext values for
/// local development convenience. Deployed environments must disable seeding or supply
/// environment-sourced secrets.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
public abstract class IdentityModuleDbSeederBase<TUser>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher) : DbSeeder
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
{
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
        await UnitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
