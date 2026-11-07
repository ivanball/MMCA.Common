using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.UI.Services.Diagnostics;

/// <summary>
/// Options for the in-app diagnostics log registered by <c>AddInAppDiagnostics</c>: how many entries
/// the in-memory buffer keeps and the lowest level it records.
/// </summary>
public sealed class InAppDiagnosticsOptions
{
    /// <summary>The default number of entries kept: 500.</summary>
    public const int DefaultCapacity = 500;

    /// <summary>
    /// Gets or sets the number of entries the buffer keeps. When full, each new entry drops the
    /// oldest one. Defaults to <see cref="DefaultCapacity"/>.
    /// </summary>
    [Range(1, 100_000)]
    public int Capacity { get; set; } = DefaultCapacity;

    /// <summary>
    /// Gets or sets the lowest level recorded. Defaults to <see cref="LogLevel.Information"/>, so
    /// Debug and Trace never reach the buffer.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
