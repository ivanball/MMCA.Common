using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// The one address normalization the auth services share. Every service that keys a cache entry
/// (a lockout, an attempt counter, a token, a request throttle) on a caller-supplied address has to
/// reduce it to the same shape the <see cref="Email"/> value object produces, or one account gets
/// independent entries per spelling. The mechanism lives here once; each service documents its own
/// reason for calling it.
/// </summary>
internal static class EmailIdentity
{
    /// <summary>
    /// Normalizes <paramref name="email"/> the way <see cref="Email"/> does: a valid address becomes
    /// the value object's normalized value, and a malformed one (which never matches a user, but can
    /// still mint a key) falls back to the same trim-and-lowercase shape so its attempts collapse
    /// onto one key too.
    /// </summary>
    /// <param name="email">The raw address from the request.</param>
    /// <returns>The normalized address, or an empty string when nothing usable was supplied.</returns>
    internal static string Normalize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        var result = Email.Create(email);
#pragma warning disable CA1308 // Matches Email's own RFC 5321 lowercase normalization.
        return result.IsSuccess ? result.Value!.Value : email.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }
}
