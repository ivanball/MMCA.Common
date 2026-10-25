using System.Text;
using System.Xml.Linq;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Gcp.DataProtection;

/// <summary>
/// Decrypts a key element <see cref="GcpKmsXmlEncryptor"/> wrote, with the Cloud KMS key the element
/// names. DataProtection activates the decryptor type recorded in the key ring itself, through the
/// <see cref="IServiceProvider"/> constructor, so the client comes from the container (the one
/// <c>AddGoogleCloudDataProtection</c> registers, or one the host registered first).
/// </summary>
internal sealed class GcpKmsXmlDecryptor : IXmlDecryptor
{
    private readonly KeyManagementServiceClient _client;

    /// <summary>Initializes a new instance; the constructor DataProtection's activator calls.</summary>
    /// <param name="services">The application services, holding the <see cref="KeyManagementServiceClient"/>.</param>
    public GcpKmsXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _client = services.GetRequiredService<KeyManagementServiceClient>();
    }

    /// <inheritdoc />
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var keyName = encryptedElement.Element(GcpKmsXmlEncryptor.KeyNameElementName)?.Value;
        var value = encryptedElement.Element(GcpKmsXmlEncryptor.ValueElementName)?.Value;
        if (string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"A Cloud KMS encrypted DataProtection key must carry both <{GcpKmsXmlEncryptor.KeyNameElementName}> and <{GcpKmsXmlEncryptor.ValueElementName}>; the key ring entry is malformed.");
        }

        var response = _client.Decrypt(new DecryptRequest
        {
            Name = keyName,
            Ciphertext = ByteString.FromBase64(value),
        });

        return XElement.Parse(Encoding.UTF8.GetString(response.Plaintext.Span));
    }
}
