using System.Globalization;
using System.Text.Json;
using Xunit;

namespace MMCA.Common.AI.Testing;

/// <summary>
/// The prompt-change protocol, as a gate: every <see cref="PromptContract"/> a repo ships is pinned
/// by hash in a small JSON file, so a prompt cannot change without somebody deciding what that
/// means for the recorded evaluations.
/// <para>
/// The two facts are the two ways the protocol is broken. A contract with no recorded hash means a
/// version was bumped and nobody recorded what the new version answers; a recorded hash that no
/// longer matches means the prompt, the model or the system text moved underneath a version that
/// claims to describe it, which silently invalidates every golden answer and every stored score
/// tagged with that version. Older versions stay in the file on purpose: they are the history of
/// what each version was.
/// </para>
/// <para>
/// The pin file is a JSON object mapping <c>"&lt;Name&gt;@&lt;Version&gt;"</c> to the lowercase hex
/// hash, resolved relative to <see cref="AppContext.BaseDirectory"/> so it is data copied next to
/// the test assembly rather than a constant compiled into it.
/// </para>
/// </summary>
public abstract class PromptContractPinTestsBase
{
    private static readonly JsonSerializerOptions PinFileOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the contracts this repo pins.</summary>
    protected abstract IEnumerable<PromptContract> Contracts { get; }

    /// <summary>
    /// Gets the pin file, relative to <see cref="AppContext.BaseDirectory"/>, for example
    /// <c>Evaluation/Golden/prompt-versions.json</c>.
    /// </summary>
    protected abstract string PinFilePath { get; }

    [Fact]
    public void EveryContract_HasARecordedHash()
    {
        var contracts = LoadContracts();
        var recorded = ReadPinFile();

        var missing = contracts
            .Where(contract => !recorded.ContainsKey(KeyOf(contract)))
            .Select(contract =>
                $"  - {KeyOf(contract)}: add this line to {PinFilePath} -> \"{KeyOf(contract)}\": \"{contract.Hash}\"")
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{missing.Count} prompt contract(s) have no recorded hash in {ResolvedPinFilePath()}. A version that nothing records is a prompt nobody evaluated:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}"));
        }
    }

    [Fact]
    public void EveryRecordedHash_MatchesTheCurrentContract()
    {
        var contracts = LoadContracts();
        var recorded = ReadPinFile();

        var drifted = contracts
            .Where(contract => recorded.TryGetValue(KeyOf(contract), out var hash)
                && !string.Equals(hash, contract.Hash, StringComparison.Ordinal))
            .Select(contract =>
                $"  - {KeyOf(contract)}: recorded {recorded[KeyOf(contract)]}, current {contract.Hash}")
            .ToList();

        if (drifted.Count > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{drifted.Count} prompt contract(s) no longer match the hash recorded in {ResolvedPinFilePath()}: the prompt, model or system text changed without a version bump: bump Version and record the new hash, or revert.{Environment.NewLine}{string.Join(Environment.NewLine, drifted)}"));
        }
    }

    private static string KeyOf(PromptContract contract) => $"{contract.Name}@{contract.Version}";

    private List<PromptContract> LoadContracts()
    {
        List<PromptContract> contracts = Contracts is null ? [] : [.. Contracts];

        if (contracts.Count == 0)
        {
            throw new InvalidOperationException(
                "No prompt contracts are declared, so this gate would pass without pinning anything. "
                + "Declare every contract this repo ships in Contracts.");
        }

        return contracts;
    }

    private string ResolvedPinFilePath() => Path.Combine(AppContext.BaseDirectory, PinFilePath);

    private Dictionary<string, string> ReadPinFile()
    {
        var path = ResolvedPinFilePath();

        // An absent pin file is not reported here: it is exactly the "nothing is recorded yet" case
        // EveryContract_HasARecordedHash reports, with the lines to add and the path to add them to.
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, PinFileOptions)
            ?? throw new InvalidOperationException(
                $"{path} did not deserialize to a map of \"<Name>@<Version>\" to hash.");
    }
}
