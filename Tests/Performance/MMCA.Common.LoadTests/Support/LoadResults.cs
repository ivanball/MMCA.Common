using System.Text.Json;

namespace MMCA.Common.LoadTests.Support;

/// <summary>
/// Writes one JSON summary per scenario, so a weekly run leaves a comparable record of every
/// measurement next to the threshold it was checked against. The directory is
/// <c>MMCA_LOAD_RESULTS_DIR</c> when set (the workflow points it at the uploaded artifact folder) and
/// <c>load-results/</c> next to the test exe otherwise (inside bin/, so never committed).
/// </summary>
internal static class LoadResults
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory =>
        Environment.GetEnvironmentVariable("MMCA_LOAD_RESULTS_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "load-results");

    public static async Task WriteAsync(string scenario, object summary, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"{scenario}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(summary, Options), cancellationToken);
    }

    /// <summary>Nearest-rank percentile (no interpolation) of an unsorted sample.</summary>
    /// <param name="samples">The measurements.</param>
    /// <param name="percentile">The percentile, 0 to 100.</param>
    /// <returns>The sample at that rank.</returns>
    public static double Percentile(IReadOnlyCollection<double> samples, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfZero(samples.Count);

        var sorted = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    public static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
