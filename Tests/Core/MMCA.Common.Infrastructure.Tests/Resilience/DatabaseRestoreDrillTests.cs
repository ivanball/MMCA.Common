using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MMCA.Common.Infrastructure.Tests.Resilience;

/// <summary>
/// In-repo disaster-recovery restore drill (rubric §29 / ADR-009): a runnable, CI-gated smoke that
/// exercises the full recovery procedure end to end — seed a database, take a backup, simulate
/// catastrophic data loss, restore from the backup, and verify zero data loss — against an ephemeral
/// SQLite database. This is the central, automated analog of a consumer's cloud restore drill (e.g.
/// MMCA.ADC's Azure SQL point-in-time-restore <c>dr-restore-drill.ps1</c>): the framework demonstrates
/// and verifies the restore story here, rather than only inheriting it downstream. The measured restore
/// duration is recorded as a baseline RTO data point (see <c>RESILIENCE.md</c>); the assertion ceiling
/// is a deliberately generous hang-detector, not a performance gate.
/// </summary>
public sealed class DatabaseRestoreDrillTests(ITestOutputHelper output)
{
    private const int SeededRowCount = 500;

    // Generous ceiling: a backup+restore of a few-hundred-row SQLite DB completes in well under a
    // second locally, so this only trips if the procedure hangs. The real RTO baseline lives in
    // RESILIENCE.md and is reported via test output below.
    private static readonly TimeSpan RtoCeiling = TimeSpan.FromSeconds(30);

    [Fact]
    public void RestoreDrill_RecoversEveryRow_AfterSimulatedDataLoss()
    {
        var result = ExecuteRestoreDrill();

        result.RowsAfterLoss.Should().Be(0, "the simulated disaster must actually destroy the live data");
        result.RowsAfterRestore.Should().Be(SeededRowCount, "the restore must recover every seeded row with no loss");
        result.IntegrityMatched.Should().BeTrue("restored values must match the pre-disaster baseline exactly");
    }

    [Fact]
    public void RestoreDrill_CompletesWithinBaselineRto()
    {
        var result = ExecuteRestoreDrill();

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Restore RTO (measured): {result.RestoreDuration.TotalMilliseconds:F1} ms for {SeededRowCount} rows"));

        result.RestoreDuration.Should().BeLessThan(RtoCeiling,
            "the restore procedure must complete within a bounded recovery-time objective");
    }

    private static DrillResult ExecuteRestoreDrill()
    {
        var primaryPath = Path.Combine(Path.GetTempPath(), $"mmca-dr-primary-{Guid.NewGuid():N}.db");
        var backupPath = Path.Combine(Path.GetTempPath(), $"mmca-dr-backup-{Guid.NewGuid():N}.db");

        try
        {
            var baseline = SeedDatabase(primaryPath);

            // 1. Take the recovery backup via SQLite's online-backup API.
            CopyDatabase(primaryPath, backupPath);

            // 2. Simulate a catastrophe: wipe the live data.
            using (var primary = Open(primaryPath))
            using (var delete = primary.CreateCommand())
            {
                delete.CommandText = "DELETE FROM DataSubject;";
                delete.ExecuteNonQuery();
            }

            var rowsAfterLoss = CountRows(primaryPath);

            // 3. Restore from the backup, timing the recovery (RTO).
            var stopwatch = Stopwatch.StartNew();
            CopyDatabase(backupPath, primaryPath);
            stopwatch.Stop();

            // 4. Verify zero data loss.
            var restored = ReadEmails(primaryPath);
            var integrityMatched = restored.SequenceEqual(baseline, StringComparer.Ordinal);

            return new DrillResult(rowsAfterLoss, restored.Count, integrityMatched, stopwatch.Elapsed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(primaryPath);
            TryDelete(backupPath);
        }
    }

    private static List<string> SeedDatabase(string path)
    {
        using var connection = Open(path);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE DataSubject (Id INTEGER PRIMARY KEY, Email TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO DataSubject (Id, Email) VALUES ($id, $email);";
        var idParam = insert.CreateParameter();
        idParam.ParameterName = "$id";
        var emailParam = insert.CreateParameter();
        emailParam.ParameterName = "$email";
        insert.Parameters.Add(idParam);
        insert.Parameters.Add(emailParam);

        var emails = new List<string>(SeededRowCount);
        for (var i = 0; i < SeededRowCount; i++)
        {
            var email = string.Create(CultureInfo.InvariantCulture, $"subject-{i}@example.com");
            idParam.Value = i;
            emailParam.Value = email;
            insert.ExecuteNonQuery();
            emails.Add(email);
        }

        transaction.Commit();
        return emails;
    }

    // Overwrites the destination database with a byte-faithful copy of the source via the SQLite
    // online-backup API — the same primitive a real backup or restore step uses.
    private static void CopyDatabase(string sourcePath, string destinationPath)
    {
        using var source = Open(sourcePath);
        using var destination = Open(destinationPath);
        source.BackupDatabase(destination);
    }

    private static List<string> ReadEmails(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Email FROM DataSubject ORDER BY Id;";
        using var reader = command.ExecuteReader();

        var emails = new List<string>();
        while (reader.Read())
        {
            emails.Add(reader.GetString(0));
        }

        return emails;
    }

    private static int CountRows(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DataSubject;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp-file cleanup.
        }
    }

    private sealed record DrillResult(int RowsAfterLoss, int RowsAfterRestore, bool IntegrityMatched, TimeSpan RestoreDuration);
}
