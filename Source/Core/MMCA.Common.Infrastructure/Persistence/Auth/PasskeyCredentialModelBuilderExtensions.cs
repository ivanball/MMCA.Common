using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// Maps the <see cref="PasskeyCredential"/> table into a consumer's model (ADR-139). Applied by
/// <c>ApplicationDbContext</c> to the one context whose source is named by
/// <c>Passkeys:DataSourceName</c>, and only while <c>Passkeys:Enabled</c> is true, so a host that never
/// opts in keeps a byte-identical model (the view-as grants precedent).
/// </summary>
public static class PasskeyCredentialModelBuilderExtensions
{
    /// <summary>The table name the configuration maps to.</summary>
    public const string TableName = "PasskeyCredentials";

    /// <summary>The unique index on the WebAuthn credential id (the sign-in lookup).</summary>
    public const string CredentialIdIndexName = "IX_PasskeyCredentials_CredentialId";

    /// <summary>The index on the owning account (the passkey list).</summary>
    public const string UserIdIndexName = "IX_PasskeyCredentials_UserId";

    /// <summary>Configures <see cref="PasskeyCredential"/> on the given model.</summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="schema">Table schema; defaults to <c>dbo</c>, matching the framework's own tables.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyPasskeyCredentialConfiguration(this ModelBuilder modelBuilder, string? schema = "dbo")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<PasskeyCredential>(entity =>
        {
            entity.ToTable(TableName, schema);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            // Bounded so the unique index fits the engine's key-size limit; WebAuthn caps a credential
            // id at 1023 bytes.
            entity.Property(e => e.CredentialId).IsRequired().HasMaxLength(PasskeyCredential.CredentialIdMaxLength);
            entity.Property(e => e.PublicKey).IsRequired().HasMaxLength(PasskeyCredential.PublicKeyMaxLength);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(PasskeyCredential.NameMaxLength);
            entity.Property(e => e.Transports).HasMaxLength(PasskeyCredential.TransportsMaxLength);
            entity.Property(e => e.SignCount);
            entity.Property(e => e.CreatedOn);
            entity.Property(e => e.LastUsedOn);

            // Sign-in path: the assertion names the credential, never the account.
            entity.HasIndex(e => e.CredentialId).IsUnique().HasDatabaseName(CredentialIdIndexName);

            // List path: one account's passkeys.
            entity.HasIndex(e => e.UserId).HasDatabaseName(UserIdIndexName);
        });

        return modelBuilder;
    }
}
