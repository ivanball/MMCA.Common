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

    /// <summary>A parameter (identified by its location and name) the baseline declared for an operation is gone.</summary>
    RemovedParameter,

    /// <summary>A parameter the baseline declared as optional is now required.</summary>
    ParameterNewlyRequired,

    /// <summary>An operation declares a required parameter the baseline did not have.</summary>
    AddedRequiredParameter,

    /// <summary>A parameter's schema now declares a different JSON type.</summary>
    ChangedParameterType,

    /// <summary>An operation that accepted a request body no longer declares one.</summary>
    RemovedRequestBody,

    /// <summary>An operation that took no request body now requires one.</summary>
    AddedRequiredRequestBody,

    /// <summary>A media type the baseline request body accepted is no longer accepted.</summary>
    RemovedRequestMediaType,
}
