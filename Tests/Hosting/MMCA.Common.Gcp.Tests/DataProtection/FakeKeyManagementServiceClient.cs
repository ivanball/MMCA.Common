using Google.Api.Gax.Grpc;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Grpc.Core;

namespace MMCA.Common.Gcp.Tests.DataProtection;

/// <summary>
/// A stand-in for Cloud KMS (no emulator exists): "encryption" is a reversible byte transform bound
/// to the key name, so a ciphertext only decrypts under the key that produced it, and the plaintext
/// never appears in the ciphertext. Every request is recorded.
/// </summary>
internal sealed class FakeKeyManagementServiceClient : KeyManagementServiceClient
{
    private const byte Mask = 0x5A;

    public List<EncryptRequest> EncryptRequests { get; } = [];

    public List<DecryptRequest> DecryptRequests { get; } = [];

    public override EncryptResponse Encrypt(EncryptRequest request, CallSettings? callSettings = null)
    {
        EncryptRequests.Add(request);
        var tag = System.Text.Encoding.UTF8.GetBytes(request.Name + "|");
        var masked = request.Plaintext.ToByteArray().Select(b => (byte)(b ^ Mask));
        return new EncryptResponse { Name = request.Name + "/cryptoKeyVersions/1", Ciphertext = ByteString.CopyFrom([.. tag, .. masked]) };
    }

    public override DecryptResponse Decrypt(DecryptRequest request, CallSettings? callSettings = null)
    {
        DecryptRequests.Add(request);
        var tag = System.Text.Encoding.UTF8.GetBytes(request.Name + "|");
        var ciphertext = request.Ciphertext.ToByteArray();
        if (!ciphertext.AsSpan().StartsWith(tag))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Decryption failed: the ciphertext is invalid."));
        }

        return new DecryptResponse { Plaintext = ByteString.CopyFrom([.. ciphertext.Skip(tag.Length).Select(b => (byte)(b ^ Mask))]) };
    }
}
