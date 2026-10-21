using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Encryption;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Encryption;

/// <summary>
/// L177: rotation step 3 (re-encrypt existing rows under the current key version) against a real
/// SQLite table. Change tracking compares decrypted plaintext, so an untouched encrypted property
/// is never part of the UPDATE; <see cref="EncryptedStringConverter.MarkForReEncryption"/> is what
/// makes a load-and-save actually rewrite the column.
/// </summary>
public sealed class EncryptedStringConverterReEncryptionTests : IDisposable
{
    private static readonly byte[] KeyV1 = EncryptedStringConverter.GenerateKey();
    private static readonly byte[] KeyV2 = EncryptedStringConverter.GenerateKey();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public EncryptedStringConverterReEncryptionTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task MarkForReEncryption_ThenSave_RewritesTheColumnUnderTheCurrentKeyVersion()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var writer = new VersionOneContext(_connection))
        {
            await writer.Database.EnsureCreatedAsync(ct);
            writer.Secrets.Add(new Secret { Id = 1, Value = "123-45-6789" });
            await writer.SaveChangesAsync(ct);
        }

        (await ReadStoredVersionAsync(ct)).Should().Be(1);

        await using (var rotated = new RotatedContext(_connection))
        {
            var secret = await rotated.Secrets.SingleAsync(ct);

            EncryptedStringConverter.MarkForReEncryption(rotated.Entry(secret)).Should().Be(1);
            await rotated.SaveChangesAsync(ct);
        }

        (await ReadStoredVersionAsync(ct)).Should().Be(
            2,
            "a row left on version 1 becomes unreadable the moment that key is dropped from the ring");

        await using var reader = new RotatedContext(_connection);
        (await reader.Secrets.AsNoTracking().SingleAsync(ct)).Value.Should().Be("123-45-6789");
    }

    private async Task<byte> ReadStoredVersionAsync(CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT \"Value\" FROM \"Secrets\"";
        var stored = (string)(await command.ExecuteScalarAsync(ct))!;
        return Convert.FromBase64String(stored)[0];
    }

    public sealed class Secret
    {
        public int Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Shared model; each subclass is its own EF model-cache entry, so each keeps its converter.</summary>
    public abstract class SecretContext(SqliteConnection connection, EncryptedStringConverter converter) : DbContext
    {
        public DbSet<Secret> Secrets => Set<Secret>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlite(connection);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Secret>(builder =>
            {
                builder.ToTable("Secrets");
                builder.HasKey(s => s.Id);
                builder.Property(s => s.Id).ValueGeneratedNever();
                builder.Property(s => s.Value).HasConversion(converter);
            });
    }

    /// <summary>Before rotation: one key, version 1.</summary>
    public sealed class VersionOneContext(SqliteConnection connection)
        : SecretContext(connection, new EncryptedStringConverter(KeyV1));

    /// <summary>After rotation step 1: both keys registered, version 2 current.</summary>
    public sealed class RotatedContext(SqliteConnection connection)
        : SecretContext(
            connection,
            new EncryptedStringConverter(new Dictionary<byte, byte[]> { [1] = KeyV1, [2] = KeyV2 }, currentKeyVersion: 2));
}
