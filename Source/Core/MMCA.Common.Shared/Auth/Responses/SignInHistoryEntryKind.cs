using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// What a <see cref="SignInSummaryResponse"/> row records. Serialized by name, so a client built
/// against an older list of kinds reads a newer one as an unknown string rather than a bare number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SignInHistoryEntryKind>))]
public enum SignInHistoryEntryKind
{
    /// <summary>The account holder signed in (one rotation chain of refresh sessions).</summary>
    SignIn = 0,
}
