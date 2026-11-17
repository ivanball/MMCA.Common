using System.Text;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Conformance.ServiceDiscovery;

/// <summary>
/// Reads the service-discovery edges an Aspire AppHost <c>Program.cs</c> and an Azure Container Apps
/// <c>main.bicep</c> declare, as <c>"consumer-&gt;peer"</c> strings, for
/// <see cref="AppHostBicepParityTestsBase"/>. Both files are read as TEXT: the AppHost hangs when
/// started headless and Bicep is not executable in a test run. The parse is deliberately narrow
/// (declaration lines and env-var names) so ordinary reformatting of either file does not move it.
/// </summary>
internal static partial class AppHostBicepEdgeParser
{
    /// <summary>
    /// Consumer-to-peer edges the AppHost declares, using the Aspire resource names (the string each
    /// <c>AddProject</c> call is given), not the C# variable names. Only edges between two
    /// <c>AddProject</c> resources count: a reference to Redis, a broker or a mail server injects a
    /// connection string, not a service-discovery name. <c>WithJwksDiscovery</c> is not an edge here:
    /// the service fetches the signing keys through the <c>Authentication__JwtBearer__Authority</c>
    /// address it sets, which production states as its own setting, so a peer the service also calls
    /// directly must be stated with an explicit <c>WithReference</c>.
    /// </summary>
    /// <param name="programSource">The AppHost <c>Program.cs</c> source text.</param>
    /// <returns>The set of edges.</returns>
    internal static HashSet<string> ParseAppHostEdges(string programSource)
    {
        var source = StripLineComments(programSource);

        // Variable name -> Aspire resource name, from `var x = builder.AddProject<Projects.Y>("name"`.
        var resourceNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match declaration in AddProjectPattern.Matches(source))
        {
            resourceNames[declaration.Groups["variable"].Value] = declaration.Groups["resource"].Value;
        }

        var edges = new HashSet<string>(StringComparer.Ordinal);

        // Statement-at-a-time, because a WithReference is attributed to whichever resource the
        // statement is about: either the one being declared (chained onto AddProject) or the one the
        // statement starts with (`conferenceService.WithReference(engagementService);`).
        foreach (var statement in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var consumerVariable = ConsumerVariable(statement);

            if (consumerVariable is null || !resourceNames.TryGetValue(consumerVariable, out var consumer))
            {
                continue;
            }

            var peerVariables = WithReferencePattern.Matches(statement)
                .Select(reference => reference.Groups["variable"].Value)

                // WithMmcaGateway(port, services...) and WithMmcaUi(gateway, port, services...)
                // (MMCA.Common.Aspire.Hosting) call WithReference on every resource they are handed,
                // so each resource variable in their argument list is an edge; the port literal maps
                // to no resource and is skipped by the same lookup.
                .Concat(MmcaFrontDoorPattern.Matches(statement)
                    .SelectMany(helper => helper.Groups["arguments"].Value.Split(',', StringSplitOptions.TrimEntries)));

            foreach (var peerVariable in peerVariables)
            {
                if (resourceNames.TryGetValue(peerVariable, out var peer))
                {
                    edges.Add($"{consumer}->{peer}");
                }
            }
        }

        return edges;
    }

    /// <summary>
    /// Consumer-to-peer edges the Bicep template declares. The consumer is the container app the env
    /// block belongs to, read from its own <c>name: '${prefix}-x'</c> line; the peer is the middle
    /// segment of the <c>services__{peer}__{endpoint}__0</c> variable. The endpoint segment is
    /// deliberately dropped: which endpoint of a peer is injected (http, https, a dedicated grpc
    /// listener) is a transport detail the two files legitimately state differently.
    /// </summary>
    /// <param name="bicepLines">The lines of the Bicep template.</param>
    /// <returns>The set of edges.</returns>
    internal static HashSet<string> ParseBicepEdges(IEnumerable<string> bicepLines)
    {
        var edges = new HashSet<string>(StringComparer.Ordinal);
        var insideContainerApp = false;
        string? consumer = null;

        foreach (var line in bicepLines)
        {
            if (ContainerAppResourcePattern.IsMatch(line))
            {
                // A new container app starts here; its name arrives on the first '${prefix}-x' line
                // inside the block, which is the resource's own name.
                insideContainerApp = true;
                consumer = null;
                continue;
            }

            if (line.StartsWith("resource ", StringComparison.Ordinal)
                || line.StartsWith("module ", StringComparison.Ordinal))
            {
                // Some other top-level declaration: stop attributing env vars to the previous app.
                insideContainerApp = false;
                consumer = null;
                continue;
            }

            if (!insideContainerApp)
            {
                continue;
            }

            if (consumer is null)
            {
                var name = ContainerAppNamePattern.Match(line);
                if (name.Success)
                {
                    consumer = name.Groups["app"].Value;
                }

                continue;
            }

            var discovery = ServiceDiscoveryEnvPattern.Match(line);
            if (discovery.Success)
            {
                edges.Add($"{consumer}->{discovery.Groups["peer"].Value}");
            }
        }

        return edges;
    }

    /// <summary>
    /// The AppHost resource variable one statement is ABOUT: the one it declares when the statement
    /// is an <c>AddProject</c> declaration, otherwise the identifier it starts with.
    /// </summary>
    /// <param name="statement">One semicolon-delimited chunk of the AppHost source.</param>
    /// <returns>The variable name, or <see langword="null"/> when the statement names none.</returns>
    private static string? ConsumerVariable(string statement)
    {
        var declaration = AddProjectPattern.Match(statement);
        if (declaration.Success)
        {
            return declaration.Groups["variable"].Value;
        }

        var subject = StatementSubjectPattern.Match(statement);
        return subject.Success ? subject.Groups["variable"].Value : null;
    }

    /// <summary>
    /// Removes <c>//</c> line comments while leaving string literals intact, so prose in the
    /// AppHost's comments cannot be mistaken for a wiring call.
    /// </summary>
    /// <param name="source">The C# source text.</param>
    /// <returns>The source with line comments removed.</returns>
    private static string StripLineComments(string source)
    {
        var stripped = new StringBuilder(source.Length);

        foreach (var line in source.Split('\n'))
        {
            var inString = false;

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                {
                    inString = !inString;
                }
                else if (!inString && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }

                stripped.Append(line[i]);
            }

            stripped.Append('\n');
        }

        return stripped.ToString();
    }

    /// <summary>Matches an AppHost project declaration, capturing the variable and resource names.</summary>
    [GeneratedRegex(
        @"var\s+(?<variable>\w+)\s*=\s*builder\.AddProject<[^>]+>\(\s*""(?<resource>[^""]+)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 5000)]
    private static partial Regex AddProjectPattern { get; }

    /// <summary>Matches the leading identifier of a statement that calls a method on an existing resource.</summary>
    [GeneratedRegex(@"^\s*(?<variable>\w+)\s*\.", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex StatementSubjectPattern { get; }

    /// <summary>Matches a <c>WithReference(variable)</c> call, capturing the referenced variable.</summary>
    [GeneratedRegex(@"WithReference\(\s*(?<variable>\w+)\s*\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex WithReferencePattern { get; }

    /// <summary>
    /// Matches a <c>WithMmcaGateway(...)</c> or <c>WithMmcaUi(...)</c> call, capturing its argument
    /// list (every resource argument is referenced by the helper).
    /// </summary>
    [GeneratedRegex(@"WithMmca(Gateway|Ui)\((?<arguments>[^)]*)\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex MmcaFrontDoorPattern { get; }

    /// <summary>Matches the opening line of a container-app resource declaration in Bicep.</summary>
    [GeneratedRegex(@"^resource\s+\w+\s+'Microsoft\.App/containerApps@", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ContainerAppResourcePattern { get; }

    /// <summary>
    /// Matches a container app's own <c>name: '${prefix}-x'</c> line (any interpolated prefix
    /// parameter), capturing the suffix.
    /// </summary>
    [GeneratedRegex(@"^\s*name:\s*'\$\{\w+\}-(?<app>[\w-]+)'\s*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ContainerAppNamePattern { get; }

    /// <summary>Matches a <c>services__peer__endpoint__0</c> env-var name, capturing the peer.</summary>
    [GeneratedRegex(@"'services__(?<peer>[\w-]+?)__\w+__\d+'", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ServiceDiscoveryEnvPattern { get; }
}
