using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMCA.Common.Passkeys.Tests;

/// <summary>
/// A software WebAuthn authenticator for the ceremony tests: an EC P-256 key pair, a <c>none</c>
/// attestation for registration and ES256 assertions for sign-in, built byte by byte from the
/// WebAuthn Level 2 layouts (authenticator data, COSE key, CBOR attestation object) so the server
/// verifies exactly what a browser would hand it.
/// </summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    // Authenticator-data flags (WebAuthn 6.1): user present, user verified, attested credential data.
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _relyingPartyId;
    private readonly string _origin;

    public SoftwareAuthenticator(string relyingPartyId, string origin)
    {
        _relyingPartyId = relyingPartyId;
        _origin = origin;
    }

    /// <summary>Gets the credential id this authenticator registers.</summary>
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>Gets the user handle the registration options carried, returned on every assertion.</summary>
    public byte[] UserHandle { get; private set; } = [];

    /// <summary>Answers creation options with a <c>none</c> attestation (the RegistrationResponseJSON shape).</summary>
    public JsonElement CreateAttestation(JsonElement creationOptions, uint signCount = 0)
    {
        var challenge = creationOptions.GetProperty("challenge").GetString()!;
        UserHandle = Base64Url.DecodeFromChars(creationOptions.GetProperty("user").GetProperty("id").GetString());

        var clientDataJson = ClientData("webauthn.create", challenge);
        var authenticatorData = AuthenticatorData(
            UserPresent | UserVerified | AttestedCredentialData, signCount, includeCredential: true);

        var attestationObject = Cbor.Map(
            (Cbor.Text("fmt"), Cbor.Text("none")),
            (Cbor.Text("attStmt"), Cbor.Map()),
            (Cbor.Text("authData"), Cbor.Bytes(authenticatorData)));

        var json = new JsonObject
        {
            ["id"] = Base64Url.EncodeToString(CredentialId),
            ["rawId"] = Base64Url.EncodeToString(CredentialId),
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["attestationObject"] = Base64Url.EncodeToString(attestationObject),
                ["clientDataJSON"] = Base64Url.EncodeToString(clientDataJson),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };

        return JsonSerializer.SerializeToElement(json);
    }

    /// <summary>Answers request options with an ES256 assertion (the AuthenticationResponseJSON shape).</summary>
    public JsonElement CreateAssertion(JsonElement requestOptions, uint signCount)
    {
        var challenge = requestOptions.GetProperty("challenge").GetString()!;
        var clientDataJson = ClientData("webauthn.get", challenge);
        var authenticatorData = AuthenticatorData(UserPresent | UserVerified, signCount, includeCredential: false);

        // The signature covers authenticatorData || SHA-256(clientDataJSON), DER-encoded for ES256.
        var signed = authenticatorData.Concat(SHA256.HashData(clientDataJson)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var json = new JsonObject
        {
            ["id"] = Base64Url.EncodeToString(CredentialId),
            ["rawId"] = Base64Url.EncodeToString(CredentialId),
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = Base64Url.EncodeToString(authenticatorData),
                ["clientDataJSON"] = Base64Url.EncodeToString(clientDataJson),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = Base64Url.EncodeToString(UserHandle),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };

        return JsonSerializer.SerializeToElement(json);
    }

    public void Dispose() => _key.Dispose();

    private byte[] ClientData(string type, string challenge) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new JsonObject
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = _origin,
            ["crossOrigin"] = false,
        }));

    private byte[] AuthenticatorData(byte flags, uint signCount, bool includeCredential)
    {
        var data = new List<byte>();
        data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(_relyingPartyId)));
        data.Add(flags);
        data.AddRange(BigEndian(signCount));
        if (includeCredential)
        {
            data.AddRange(new byte[16]); // AAGUID: none for a software authenticator.
            data.Add((byte)(CredentialId.Length >> 8));
            data.Add((byte)CredentialId.Length);
            data.AddRange(CredentialId);
            data.AddRange(CosePublicKey());
        }

        return [.. data];
    }

    // COSE_Key (RFC 9053): kty EC2 (2), alg ES256 (-7), crv P-256 (1), x, y.
    private byte[] CosePublicKey()
    {
        var point = _key.ExportParameters(includePrivateParameters: false).Q;
        return Cbor.Map(
            (Cbor.Int(1), Cbor.Int(2)),
            (Cbor.Int(3), Cbor.Int(-7)),
            (Cbor.Int(-1), Cbor.Int(1)),
            (Cbor.Int(-2), Cbor.Bytes(point.X!)),
            (Cbor.Int(-3), Cbor.Bytes(point.Y!)));
    }

    private static byte[] BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>The handful of CBOR (RFC 8949) shapes the WebAuthn structures need.</summary>
    private static class Cbor
    {
        public static byte[] Int(int value) =>
            value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value)
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            return [.. Head(3, (ulong)utf8.Length), .. utf8];
        }

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) =>
            [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(e => e.Key.Concat(e.Value))];

        private static byte[] Head(int majorType, ulong argument)
        {
            var major = (byte)(majorType << 5);
            return argument switch
            {
                < 24 => [(byte)(major | (byte)argument)],
                <= byte.MaxValue => [(byte)(major | 24), (byte)argument],
                <= ushort.MaxValue => [(byte)(major | 25), (byte)(argument >> 8), (byte)argument],
                _ => throw new ArgumentOutOfRangeException(nameof(argument)),
            };
        }
    }
}
