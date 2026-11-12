using Microsoft.Extensions.Configuration;

namespace MMCA.Common.Aws.Telemetry;

/// <summary>
/// The <c>AwsTelemetry</c> configuration section <c>AddAwsTelemetryExporter()</c> reads.
/// <c>Region</c> is the gate; every other key is optional.
/// </summary>
internal sealed class AwsTelemetrySettings
{
    /// <summary>The configuration section.</summary>
    internal const string SectionPath = "AwsTelemetry";

    /// <summary>The gate: the AWS region the telemetry is written to, for example <c>eu-west-1</c>.</summary>
    public string? Region { get; init; }

    /// <summary>The CloudWatch Logs log group the log records are written to. Set together with <see cref="LogStream"/>.</summary>
    public string? LogGroup { get; init; }

    /// <summary>The CloudWatch Logs log stream the log records are written to. Set together with <see cref="LogGroup"/>.</summary>
    public string? LogStream { get; init; }

    /// <summary>The full traces URL, overriding the regional X-Ray OTLP endpoint.</summary>
    public string? TracesEndpoint { get; init; }

    /// <summary>The full logs URL, overriding the regional CloudWatch Logs OTLP endpoint.</summary>
    public string? LogsEndpoint { get; init; }

    /// <summary>A static access key id. Set together with <see cref="SecretAccessKey"/>, or neither for the default chain.</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>A static secret access key. Set together with <see cref="AccessKeyId"/>, or neither for the default chain.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>Reads the section from configuration, key by key.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The settings; every value may be null.</returns>
    internal static AwsTelemetrySettings Read(IConfiguration configuration) => new()
    {
        Region = configuration[Key(nameof(Region))],
        LogGroup = configuration[Key(nameof(LogGroup))],
        LogStream = configuration[Key(nameof(LogStream))],
        TracesEndpoint = configuration[Key(nameof(TracesEndpoint))],
        LogsEndpoint = configuration[Key(nameof(LogsEndpoint))],
        AccessKeyId = configuration[Key(nameof(AccessKeyId))],
        SecretAccessKey = configuration[Key(nameof(SecretAccessKey))],
    };

    /// <summary>The full configuration key of one setting, for example <c>AwsTelemetry:Region</c>.</summary>
    /// <param name="name">The setting name.</param>
    /// <returns>The full key.</returns>
    internal static string Key(string name) => $"{SectionPath}:{name}";
}
