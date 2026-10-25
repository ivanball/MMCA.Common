using System.Xml.Linq;
using AwesomeAssertions;
using Google.Cloud.Kms.V1;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Gcp.DataProtection;

namespace MMCA.Common.Gcp.Tests.DataProtection;

/// <summary>
/// The Cloud KMS key-ring encryption pair against a fake client (no KMS emulator exists): the
/// encryptor sends the element to the configured key and stores only the ciphertext plus the key
/// name, and the decryptor, activated the way DataProtection activates it (from the service
/// provider), recovers the original element.
/// </summary>
public sealed class GcpKmsXmlEncryptionTests
{
    private static readonly CryptoKeyName KeyName = new("mmca-prod", "global", "dataprotection", "key-ring");

    private static XElement MasterKey() =>
        new("masterKey", new XAttribute("version", "1"), new XElement("value", "c2VjcmV0LWtleS1tYXRlcmlhbA=="));

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsTheElement_WithoutThePlaintextAtRest()
    {
        var kms = new FakeKeyManagementServiceClient();
        var encryptor = new GcpKmsXmlEncryptor(kms, KeyName);

        var encrypted = encryptor.Encrypt(MasterKey());

        encrypted.DecryptorType.Should().Be<GcpKmsXmlDecryptor>();
        encrypted.EncryptedElement.ToString().Should().NotContain("c2VjcmV0LWtleS1tYXRlcmlhbA==", "only the ciphertext is stored");
        encrypted.EncryptedElement.Element("kmsKeyName")!.Value.Should().Be(KeyName.ToString());
        kms.EncryptRequests.Should().ContainSingle().Which.Name.Should().Be(KeyName.ToString());

        using var services = new ServiceCollection().AddSingleton<KeyManagementServiceClient>(kms).BuildServiceProvider();
        var decryptor = new GcpKmsXmlDecryptor(services);

        var decrypted = decryptor.Decrypt(encrypted.EncryptedElement);

        XNode.DeepEquals(decrypted, MasterKey()).Should().BeTrue();
        kms.DecryptRequests.Should().ContainSingle().Which.Name.Should().Be(KeyName.ToString(), "decryption names the key, and KMS picks the version from the ciphertext");
    }

    [Fact]
    public void Decrypt_OfAnElementWithoutItsParts_FailsNamingThem()
    {
        using var services = new ServiceCollection().AddSingleton<KeyManagementServiceClient>(new FakeKeyManagementServiceClient()).BuildServiceProvider();

        var act = () => new GcpKmsXmlDecryptor(services).Decrypt(new XElement("encryptedKey"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*kmsKeyName*value*");
    }
}
