using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Preconditions;

namespace MMCA.Common.Testing.Aspire.Tests.Preconditions;

/// <summary>
/// The development-certificate probe, exercised against certificates built here rather than against
/// whatever happens to sit in the machine's store.
/// </summary>
public sealed class DeveloperCertificateAvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidDevelopmentCertificate_IsPresent()
    {
        using var certificate = CreateCertificate(withDevelopmentExtension: true, Now.AddDays(-1), Now.AddDays(30));

        DeveloperCertificateAvailability.IsPresent([certificate], Now).Should().BeTrue();
    }

    [Fact]
    public void ExpiredDevelopmentCertificate_IsNotPresent()
    {
        using var certificate = CreateCertificate(withDevelopmentExtension: true, Now.AddDays(-60), Now.AddDays(-1));

        DeveloperCertificateAvailability.IsPresent([certificate], Now)
            .Should().BeFalse("an expired certificate fails the TLS handshake exactly as an absent one does");
    }

    [Fact]
    public void NotYetValidCertificate_IsNotPresent()
    {
        using var certificate = CreateCertificate(withDevelopmentExtension: true, Now.AddDays(1), Now.AddDays(30));

        DeveloperCertificateAvailability.IsPresent([certificate], Now).Should().BeFalse();
    }

    [Fact]
    public void OtherLocalhostCertificate_IsNotMistakenForIt()
    {
        using var certificate = CreateCertificate(withDevelopmentExtension: false, Now.AddDays(-1), Now.AddDays(30));

        DeveloperCertificateAvailability.IsPresent([certificate], Now)
            .Should().BeFalse("the SDK extension OID is the only thing that identifies the development certificate");
    }

    [Fact]
    public void EmptyStore_IsNotPresent() =>
        DeveloperCertificateAvailability.IsPresent([], Now).Should().BeFalse();

    [Fact]
    public void PublicProbe_AnswersWithoutThrowing()
    {
        // The real probe opens the current user's certificate store, which may not exist at all on a
        // container runner. Answering false is correct there; throwing is not.
        var act = () => DeveloperCertificateAvailability.IsPresent();

        act.Should().NotThrow();
    }

    private static X509Certificate2 CreateCertificate(
        bool withDevelopmentExtension,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (withDevelopmentExtension)
        {
            request.CertificateExtensions.Add(new X509Extension(
                new Oid(DeveloperCertificateAvailability.AspNetCoreHttpsDevelopmentCertificateOid),
                [1],
                critical: false));
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
