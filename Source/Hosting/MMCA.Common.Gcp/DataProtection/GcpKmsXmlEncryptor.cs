using System.Text;
using System.Xml.Linq;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace MMCA.Common.Gcp.DataProtection;

/// <summary>
/// Encrypts each DataProtection key element with a Cloud KMS symmetric key before it reaches the key
/// ring, so the object at rest holds only ciphertext and reading a key needs <c>cloudkms.cryptoKeyVersions.useToDecrypt</c>
/// on top of read access to the bucket. The element is sent to KMS whole (a key element is about a
/// kilobyte, far under the 64 KiB plaintext limit); the stored form names the key and carries the
/// ciphertext:
/// <code>
/// &lt;encryptedKey&gt;
///   &lt;!-- This key is encrypted with Google Cloud KMS. --&gt;
///   &lt;kmsKeyName&gt;projects/.../cryptoKeys/...&lt;/kmsKeyName&gt;
///   &lt;value&gt;base64 ciphertext&lt;/value&gt;
/// &lt;/encryptedKey&gt;
/// </code>
/// The ciphertext records the key version, so rotating the KMS key needs no change here and old keys
/// keep decrypting while their version is enabled. <see cref="GcpKmsXmlDecryptor"/> reverses it.
/// </summary>
internal sealed class GcpKmsXmlEncryptor : IXmlEncryptor
{
    /// <summary>The root of the stored form.</summary>
    internal const string EncryptedKeyElementName = "encryptedKey";

    /// <summary>The element that names the KMS key.</summary>
    internal const string KeyNameElementName = "kmsKeyName";

    /// <summary>The element that carries the base64 ciphertext.</summary>
    internal const string ValueElementName = "value";

    private readonly KeyManagementServiceClient _client;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="client">The Cloud KMS client.</param>
    /// <param name="keyName">The symmetric key that encrypts the key ring.</param>
    internal GcpKmsXmlEncryptor(KeyManagementServiceClient client, CryptoKeyName keyName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        KeyName = keyName ?? throw new ArgumentNullException(nameof(keyName));
    }

    /// <summary>Gets the symmetric key that encrypts the key ring.</summary>
    internal CryptoKeyName KeyName { get; }

    /// <inheritdoc />
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var response = _client.Encrypt(new EncryptRequest
        {
            Name = KeyName.ToString(),
            Plaintext = ByteString.CopyFrom(plaintext),
        });

        var encrypted = new XElement(
            EncryptedKeyElementName,
            new XComment(" This key is encrypted with Google Cloud KMS. "),
            new XElement(KeyNameElementName, KeyName.ToString()),
            new XElement(ValueElementName, response.Ciphertext.ToBase64()));

        return new EncryptedXmlInfo(encrypted, typeof(GcpKmsXmlDecryptor));
    }
}
