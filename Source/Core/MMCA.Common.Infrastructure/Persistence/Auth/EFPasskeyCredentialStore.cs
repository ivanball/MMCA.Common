using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// EF Core <see cref="IPasskeyCredentialStore"/> over the consumer's Identity database (ADR-139).
/// <para>
/// <b>Which database.</b> Resolved like <see cref="EFViewAsGrantStore"/>: the data-source registry
/// first, falling back to the source named by <c>Passkeys:DataSourceName</c>.
/// </para>
/// <para>
/// Registered only by <c>AddPasskeys</c>, and every caller is behind the <c>Passkeys:Enabled</c> gate,
/// so it never queries a table the model does not carry.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Resolves and saves through the Identity context.</param>
/// <param name="registry">Entity-to-source registry, consulted first.</param>
/// <param name="dataSourceResolver">Resolves the logical default source's engine.</param>
/// <param name="settings">Bound passkey settings.</param>
internal sealed class EFPasskeyCredentialStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IDataSourceResolver dataSourceResolver,
    IOptions<PasskeySettings> settings) : IPasskeyCredentialStore
{
    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<PasskeyCredential> Credentials => Context.Set<PasskeyCredential>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<PasskeyCredential>> ListForUserAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        await Credentials
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<PasskeyCredential?> FindAsync(UserIdentifierType userId, Guid id, CancellationToken cancellationToken = default) =>
        Credentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public Task<PasskeyCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
        Credentials.FirstOrDefaultAsync(c => c.CredentialId == credentialId, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        await Credentials.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // The row came from FindAsync or FindByCredentialIdAsync, which track it, so the change is
        // already known to the context; the save is all that is left.
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        Credentials.Remove(credential);
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // The configured NAME is used verbatim; only the engine goes through the resolver (the
    // EFViewAsGrantStore precedent).
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(PasskeyCredential).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
