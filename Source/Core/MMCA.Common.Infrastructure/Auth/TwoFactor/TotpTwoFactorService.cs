using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.TwoFactor;
using OtpNet;

namespace MMCA.Common.Infrastructure.Auth.TwoFactor;

/// <summary>
/// RFC 6238 implementation of <see cref="ITwoFactorService"/> over <c>Otp.NET</c>, plus the recovery
/// codes and their hash-at-rest treatment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recovery codes are hashed the way reset tokens are</b> (SHA-256, hex, upper case, matching
/// <c>RefreshSession.HashToken</c>): a plain digest with no key derivation, which is right precisely
/// because the codes are high-entropy random values rather than user-chosen secrets. Stretching a
/// value with eighty bits of entropy buys nothing an attacker cannot skip, and the cheap digest keeps
/// the sign-in path from paying a key derivation per stored code.
/// </para>
/// <para>
/// <b>Comparison is fixed time.</b> A presented recovery code is compared against every stored hash
/// with <see cref="CryptographicOperations.FixedTimeEquals"/> and without an early exit, so the reply
/// time does not say how many codes were checked before a match.
/// </para>
/// <para>
/// The provisioning URI is composed here rather than taken from the library, because its exact shape
/// (the <c>issuer:account</c> label AND the redundant <c>issuer</c> parameter, which is what older
/// authenticator apps read) is part of what a user's app has to accept, and building it in one visible
/// place is what keeps that true across a library upgrade.
/// </para>
/// </remarks>
/// <param name="settings">The bound two-factor settings.</param>
internal sealed class TotpTwoFactorService(IOptions<TwoFactorSettings> settings) : ITwoFactorService
{
    private readonly TwoFactorSettings _settings = settings.Value;

    /// <inheritdoc />
    public string GenerateSecret() =>
        Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(_settings.SecretByteLength));

    /// <inheritdoc />
    public string BuildProvisioningUri(string secret, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        var issuer = Uri.EscapeDataString(_settings.Issuer);
        var label = $"{issuer}:{Uri.EscapeDataString(accountName)}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits={_settings.Digits}&period={_settings.PeriodSeconds}");
    }

    /// <inheritdoc />
    public bool VerifyCode(string secret, string? code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = Base32Encoding.ToBytes(secret);
        }
        catch (ArgumentException)
        {
            // A stored secret that is not Base32 can never mint a code, so it verifies nothing. That
            // is a data fault, not a caller fault, and answering false keeps it out of the login
            // path's exception surface.
            return false;
        }

        var totp = new Totp(key, step: _settings.PeriodSeconds, mode: OtpHashMode.Sha1, totpSize: _settings.Digits);
        var window = new VerificationWindow(
            previous: _settings.VerificationWindowSteps,
            future: _settings.VerificationWindowSteps);

        return totp.VerifyTotp(NormalizeCode(code), out _, window);
    }

    /// <inheritdoc />
    public RecoveryCodeSet GenerateRecoveryCodes()
    {
        var codes = new List<string>(_settings.RecoveryCodeCount);
        var hashes = new List<string>(_settings.RecoveryCodeCount);

        for (var index = 0; index < _settings.RecoveryCodeCount; index++)
        {
            // Base32 rather than Base64: the alphabet has no case ambiguity and no characters a user
            // has to guess at while copying a code off paper.
            var code = Base32Encoding
                .ToString(RandomNumberGenerator.GetBytes(_settings.RecoveryCodeByteLength))
                .TrimEnd('=');

            codes.Add(code);
            hashes.Add(HashRecoveryCode(code));
        }

        return new RecoveryCodeSet(codes, hashes);
    }

    /// <inheritdoc />
    public string HashRecoveryCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code))));
    }

    /// <inheritdoc />
    public bool TryMatchRecoveryCode(string? code, IReadOnlyCollection<string> storedHashes, out string? matchedHash)
    {
        ArgumentNullException.ThrowIfNull(storedHashes);

        matchedHash = null;

        if (string.IsNullOrWhiteSpace(code) || storedHashes.Count == 0)
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(HashRecoveryCode(code));

        foreach (var stored in storedHashes)
        {
            if (stored is null)
            {
                continue;
            }

            // No early exit on the first match: the loop runs to the end so the answer takes the same
            // time whichever code in the list was presented.
            if (CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(stored)))
            {
                matchedHash = stored;
            }
        }

        return matchedHash is not null;
    }

    /// <summary>
    /// Strips the whitespace and separators a user types or a password manager inserts, and upper
    /// cases what is left, so a code is matched the way it was generated.
    /// </summary>
    /// <param name="code">The presented code.</param>
    /// <returns>The normalized code.</returns>
    private static string NormalizeCode(string code) =>
        string.Concat(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant));
}
