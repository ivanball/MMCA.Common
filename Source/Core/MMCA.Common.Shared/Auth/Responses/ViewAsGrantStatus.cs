using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// Where a view-as grant stands (ADR-137), derived from its end and expiry instants rather than
/// stored. Serialized by name, so a client reads a newer status as an unknown string rather than a
/// bare number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ViewAsGrantStatus>))]
public enum ViewAsGrantStatus
{
    /// <summary>Neither ended nor expired: the staff user may still be viewing.</summary>
    Live = 0,

    /// <summary>The staff user ended the view before it expired.</summary>
    Ended = 1,

    /// <summary>The view ran out its lifetime without being ended.</summary>
    Expired = 2,
}
