namespace MMCA.Common.Testing.Conformance;

/// <summary>The kinds of change <see cref="OpenApiBreakingChangeDetector"/> treats as breaking.</summary>
public enum OpenApiBreakingChangeKind
{
    /// <summary>A path the baseline describes is gone.</summary>
    RemovedPath,

    /// <summary>An operation (HTTP method) of a path the baseline describes is gone.</summary>
    RemovedOperation,

    /// <summary>A response status code an operation declared is no longer declared.</summary>
    RemovedResponseStatusCode,

    /// <summary>A request body property is now required that the baseline did not require.</summary>
    RequestPropertyNewlyRequired,

    /// <summary>A response body property the baseline described is gone.</summary>
    RemovedResponseProperty,

    /// <summary>A property (or a body schema) now declares a different JSON type.</summary>
    ChangedPropertyType,
}
