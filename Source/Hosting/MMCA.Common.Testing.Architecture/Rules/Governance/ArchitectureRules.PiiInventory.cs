namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The header cell that names the member a personal-data inventory row describes.</summary>
    private const string InventoryMemberColumn = "Member";

    /// <summary>
    /// The three cells every personal-data inventory row must fill: why the data is held, the lawful
    /// basis for holding it, and how long it is kept.
    /// </summary>
    private static readonly string[] InventoryRequiredColumns = ["Purpose", "Lawful basis", "Retention"];

    /// <summary>
    /// Personal-data inventory, code to document: every property carrying <c>[Pii]</c> in
    /// <paramref name="assemblies"/> has a row in the <c>PRIVACY.md</c> inventory table, and that row's
    /// <c>Purpose</c>, <c>Lawful basis</c> and <c>Retention</c> cells are filled. A member marked as
    /// personal data that the policy does not account for is data held without a stated reason, basis
    /// or end date.
    /// </summary>
    /// <param name="assemblies">The assemblies whose <c>[Pii]</c> members the inventory must cover.</param>
    /// <param name="privacyPolicyMarkdown">The full text of the repo's <c>PRIVACY.md</c>.</param>
    public static void PiiMembersHaveInventoryRows(IEnumerable<Assembly> assemblies, string privacyPolicyMarkdown)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(privacyPolicyMarkdown);

        var rows = ParsePiiInventory(privacyPolicyMarkdown);
        var violations = new List<string>();

        foreach (var member in PiiMemberKeys(assemblies))
        {
            var row = rows.Find(r => string.Equals(r.Member, member, StringComparison.Ordinal));
            if (row is null)
            {
                violations.Add($"  - {member} has no row in the PRIVACY.md personal-data inventory");
                continue;
            }

            violations.AddRange(InventoryRequiredColumns
                .Where(column => string.IsNullOrWhiteSpace(row.Cells.GetValueOrDefault(column)))
                .Select(column => $"  - {member}: the inventory row has an empty '{column}' cell"));
        }

        ArchitectureAssert.NoViolations(
            violations,
            "every [Pii] member must have a PRIVACY.md inventory row stating its purpose, lawful basis and "
                + "retention, so personal data is never held without a stated reason, basis or end date. The "
                + "inventory is a Markdown table whose header holds the columns 'Member | Purpose | Lawful "
                + "basis | Retention', one row per member named 'Type.Property'");
    }

    /// <summary>
    /// Personal-data inventory, document to code: every row of the <c>PRIVACY.md</c> inventory table
    /// names a property that still carries <c>[Pii]</c> in <paramref name="assemblies"/>. An orphan row
    /// is a promise about data the code no longer marks (renamed, removed, or quietly unmarked), so
    /// the published policy and the system have drifted apart.
    /// </summary>
    /// <param name="assemblies">The assemblies whose <c>[Pii]</c> members the inventory describes.</param>
    /// <param name="privacyPolicyMarkdown">The full text of the repo's <c>PRIVACY.md</c>.</param>
    public static void PiiInventoryRowsMapToPiiMembers(IEnumerable<Assembly> assemblies, string privacyPolicyMarkdown)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(privacyPolicyMarkdown);

        var members = PiiMemberKeys(assemblies).ToHashSet(StringComparer.Ordinal);
        var violations = ParsePiiInventory(privacyPolicyMarkdown)
            .Where(row => !members.Contains(row.Member))
            .Select(row => $"  - inventory row '{row.Member}' names no live [Pii] member");

        ArchitectureAssert.NoViolations(
            violations,
            "every PRIVACY.md inventory row must name a live [Pii] member ('Type.Property'): an orphan row "
                + "describes data the code no longer marks, so remove the row or restore the marker in the "
                + "same change");
    }

    /// <summary>One row of the personal-data inventory: the member it names and its cells by header.</summary>
    private sealed record PiiInventoryRow(string Member, IReadOnlyDictionary<string, string> Cells);

    /// <summary>
    /// The <c>Type.Property</c> key of every property declared with <c>[Pii]</c> (matched by name) in
    /// the assemblies, where <c>Type</c> is the declaring type's simple name without its generic arity.
    /// </summary>
    private static IEnumerable<string> PiiMemberKeys(IEnumerable<Assembly> assemblies) =>
        assemblies
            .Distinct()
            .SelectMany(a => a.LoadableTypes)
            .Where(t => !t.IsInterface && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttributes(inherit: false).Any(a => string.Equals(a.GetType().Name, "PiiAttribute", StringComparison.Ordinal)))
                .Select(p => $"{t.SimpleName}.{p.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Reads every inventory table in the Markdown: a table whose header row holds the
    /// <c>Member</c>, <c>Purpose</c>, <c>Lawful basis</c> and <c>Retention</c> columns (any order,
    /// case-insensitive, extra columns allowed). Backticks around the member name are ignored. A
    /// table ends at the first line that does not start with a pipe.
    /// </summary>
    private static List<PiiInventoryRow> ParsePiiInventory(string markdown)
    {
        var rows = new List<PiiInventoryRow>();
        string[]? header = null;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|'))
            {
                header = null;
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (header is null)
            {
                header = IsInventoryHeader(cells) ? cells : null;
                continue;
            }

            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                continue;
            }

            var byColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++)
            {
                byColumn[header[i]] = i < cells.Length ? cells[i] : string.Empty;
            }

            rows.Add(new PiiInventoryRow(byColumn[InventoryMemberColumn].Trim('`').Trim(), byColumn));
        }

        return rows;
    }

    /// <summary>True when a table header row carries the member column and all three required columns.</summary>
    private static bool IsInventoryHeader(string[] cells) =>
        InventoryRequiredColumns.Append(InventoryMemberColumn)
            .All(column => cells.Contains(column, StringComparer.OrdinalIgnoreCase));
}
