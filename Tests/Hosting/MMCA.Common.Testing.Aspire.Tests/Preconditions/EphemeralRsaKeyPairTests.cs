using System.Security.Cryptography;
using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Preconditions;

namespace MMCA.Common.Testing.Aspire.Tests.Preconditions;

/// <summary>
/// The generated key material is what an Identity resource signs with, so "it produced two strings"
/// is not the claim: the PEMs must import, pair with each other, and be a fresh pair each time.
/// </summary>
public sealed class EphemeralRsaKeyPairTests
{
    [Fact]
    public void Create_ProducesImportablePem()
    {
        var keys = EphemeralRsaKeyPair.Create();

        using var privateKey = RSA.Create();
        using var publicKey = RSA.Create();
        var importPrivate = () => privateKey.ImportFromPem(keys.PrivateKeyPem);
        var importPublic = () => publicKey.ImportFromPem(keys.PublicKeyPem);

        importPrivate.Should().NotThrow();
        importPublic.Should().NotThrow();
        privateKey.KeySize.Should().Be(EphemeralRsaKeyPair.KeySizeInBits);
    }

    [Fact]
    public void Create_PairsThePublicKeyWithThePrivateOne()
    {
        var keys = EphemeralRsaKeyPair.Create();

        using var signer = RSA.Create();
        signer.ImportFromPem(keys.PrivateKeyPem);
        using var verifier = RSA.Create();
        verifier.ImportFromPem(keys.PublicKeyPem);

        var payload = "the JWKS document must validate a token this key signed"u8.ToArray();
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue("a mismatched pair would answer JWKS with a key nothing signed with");
    }

    [Fact]
    public void Create_IsFreshEveryTime() =>
        EphemeralRsaKeyPair.Create().PrivateKeyPem
            .Should().NotBe(
                EphemeralRsaKeyPair.Create().PrivateKeyPem,
                "the key is throwaway per collection, so it must never be a constant in disguise");

    [Fact]
    public void VariableNames_AreTheChannelTheAppHostExtensionReads()
    {
        // WithE2eRsaKeys() reads exactly these two names off the process environment. They are the
        // contract between this package and MMCA.Common.Aspire.Hosting, so pin them.
        EphemeralRsaKeyPair.PrivateKeyVariable.Should().Be("E2E_JWT_PRIVATE_KEY_PEM");
        EphemeralRsaKeyPair.PublicKeyVariable.Should().Be("E2E_JWT_PUBLIC_KEY_PEM");
    }
}
