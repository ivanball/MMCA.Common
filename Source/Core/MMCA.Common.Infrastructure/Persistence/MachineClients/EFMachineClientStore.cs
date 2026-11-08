using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth.MachineClients;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Infrastructure.Persistence.MachineClients;

/// <summary>
/// EF Core <see cref="IMachineClientStore"/> over the consumer's Identity database (ADR-140).
/// <para>
/// <b>Which database.</b> Resolved like <c>EFPasskeyCredentialStore</c>: the data-source registry
/// first, falling back to the source named by <c>MachineClients:DataSourceName</c>.
/// </para>
/// <para>
/// Registered only by <c>AddMachineClients</c>, and every caller is behind the
/// <c>MachineClients:Enabled</c> gate, so it never queries a table the model does not carry. Writes go
/// through the unit of work, so the audit interceptor stamps who created, rotated or revoked a client.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Resolves and saves through the Identity context.</param>
/// <param name="registry">Entity-to-source registry, consulted first.</param>
/// <param name="dataSourceResolver">Resolves the logical default source's engine.</param>
/// <param name="settings">Bound machine-client settings.</param>
internal sealed class EFMachineClientStore(
    IDbContextFactory dbContextFactory,
    IEntityDataSourceRegistry registry,
    IDataSourceResolver dataSourceResolver,
    IOptions<MachineClientSettings> settings) : IMachineClientStore
{
    private ApplicationDbContext Context => dbContextFactory.GetDbContext(ResolveDataSourceKey());

    private DbSet<MachineClient> Clients => Context.Set<MachineClient>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<MachineClient>> ListAsync(CancellationToken cancellationToken = default) =>
        await Clients
            .AsNoTracking()
            .OrderBy(c => c.CreatedOn)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<MachineClient?> FindAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Clients.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(MachineClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        await Clients.AddAsync(client, cancellationToken).ConfigureAwait(false);
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(MachineClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // The row came from FindAsync, which tracks it, so the change is already known to the
        // context; the save is all that is left.
        await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordUseAsync(MachineClient client, DateTime usedOn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.RecordUse(usedOn);
        try
        {
            await dbContextFactory.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another issuance for the same client saved between this request's read and its write
            // (the row version moved). Its LastUsedOn is as good as this one's, and the token is
            // already minted, so the conflict is dropped rather than failing the program's request.
        }
    }

    // The configured NAME is used verbatim; only the engine goes through the resolver (the
    // EFViewAsGrantStore precedent).
    private DataSourceKey ResolveDataSourceKey() =>
        registry.TryGetDataSourceKey(typeof(MachineClient).FullName!, out var key)
            ? key
            : new DataSourceKey(
                dataSourceResolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName).Engine,
                settings.Value.DataSourceName);
}
