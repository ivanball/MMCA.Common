using System.Security.Cryptography.X509Certificates;

namespace MMCA.Common.Testing.Aspire.Preconditions;

/// <summary>
/// Detects the ASP.NET Core HTTPS development certificate in the current user's personal store.
/// <para>
/// This exists because of a real production-shaped failure. A resource that launches with the
/// <c>https</c> profile answers its health probe over TLS terminated by that certificate; on a fresh
/// CI runner the certificate is absent (or present but untrusted), every probe fails with
/// <c>UntrustedRoot</c>, the resource never turns healthy, and every <c>WaitFor</c> edge into it
/// waits out the whole budget. The symptom is a timeout on an unrelated resource, which is why the
/// precondition is worth naming out loud.
/// </para>
/// <para>
/// <b>Detection only.</b> This type never runs <c>dotnet dev-certs</c> and never writes to a
/// certificate store: installing or trusting a certificate is a machine-level act that a test
/// fixture has no business performing silently. A CI job that needs one runs
/// <c>dotnet dev-certs https --trust</c> as an explicit step.
/// </para>
/// </summary>
public static class DeveloperCertificateAvailability
{
    /// <summary>
    /// The custom extension OID the .NET SDK stamps on the ASP.NET Core HTTPS development
    /// certificate, and the only reliable way to tell it apart from any other localhost certificate.
    /// </summary>
    public const string AspNetCoreHttpsDevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

    /// <summary>
    /// Whether a currently valid ASP.NET Core HTTPS development certificate sits in the current
    /// user's personal store.
    /// </summary>
    /// <returns><see langword="true"/> when such a certificate is present and not expired.</returns>
    public static bool IsPresent() => IsPresent(EnumerateCurrentUserCertificates(), DateTimeOffset.UtcNow);

    /// <summary>
    /// Testable core of <see cref="IsPresent()"/>: takes the certificates to inspect and the instant
    /// to judge validity at, so expiry can be proven without touching a real store.
    /// </summary>
    /// <param name="certificates">The certificates to inspect.</param>
    /// <param name="now">The instant validity is judged at.</param>
    /// <returns><see langword="true"/> when one of them is a valid development certificate.</returns>
    internal static bool IsPresent(IEnumerable<X509Certificate2> certificates, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        return certificates.Any(certificate =>
            certificate.Extensions.Any(extension =>
                string.Equals(extension.Oid?.Value, AspNetCoreHttpsDevelopmentCertificateOid, StringComparison.Ordinal))
            && now < new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero)
            && now >= new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero));
    }

    /// <summary>
    /// Reads the current user's personal store. A store that cannot be opened (no user profile on a
    /// container runner, a locked keychain) is reported as empty, which makes the gate skip rather
    /// than throw.
    /// </summary>
    /// <returns>The certificates found, or an empty sequence.</returns>
    private static IReadOnlyList<X509Certificate2> EnumerateCurrentUserCertificates()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            // ReadOnly is the zero flag, so OpenExistingOnly alone is a read-only open of an
            // existing store; naming both is what RCS1258 rejects.
            store.Open(OpenFlags.OpenExistingOnly);
            return [.. store.Certificates];
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
