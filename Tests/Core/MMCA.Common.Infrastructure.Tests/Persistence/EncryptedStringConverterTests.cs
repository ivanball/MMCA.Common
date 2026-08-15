using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Encryption;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class EncryptedStringConverterTests
{
    // ── Roundtrip ──
    [Fact]
    public void EncryptThenDecrypt_RoundTrip_ReturnsOriginalPlaintext()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);

        const string plaintext = "Hello, encrypted world!";
        string encrypted = converter.ConvertToProviderExpression.Compile()(plaintext);
        string decrypted = converter.ConvertFromProviderExpression.Compile()(encrypted);

        decrypted.Should().Be(plaintext);
    }

    // ── Different plaintexts produce different ciphertexts ──
    [Fact]
    public void Encrypt_DifferentPlaintexts_ProduceDifferentCiphertexts()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);
        var encrypt = converter.ConvertToProviderExpression.Compile();

        string cipher1 = encrypt("plaintext-one");
        string cipher2 = encrypt("plaintext-two");

        cipher1.Should().NotBe(cipher2);
    }

    // ── Same plaintext with different nonce produces different ciphertexts ──
    [Fact]
    public void Encrypt_SamePlaintext_ProducesDifferentCiphertextsDueToRandomNonce()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);
        var encrypt = converter.ConvertToProviderExpression.Compile();

        string cipher1 = encrypt("same-text");
        string cipher2 = encrypt("same-text");

        cipher1.Should().NotBe(cipher2);
    }

    // ── GenerateKey produces valid 32-byte key ──
    [Fact]
    public void GenerateKey_Returns32ByteArray()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();

        key.Should().HaveCount(32);
    }

    // ── GenerateKey produces different keys ──
    [Fact]
    public void GenerateKey_ProducesDifferentKeysEachTime()
    {
        byte[] key1 = EncryptedStringConverter.GenerateKey();
        byte[] key2 = EncryptedStringConverter.GenerateKey();

        key1.Should().NotEqual(key2);
    }

    // ── Invalid key length ──
    [Fact]
    public void Constructor_WithInvalidKeyLength_ThrowsArgumentException()
    {
        byte[] shortKey = new byte[16];

        FluentActions.Invoking(() => new EncryptedStringConverter(shortKey))
            .Should().Throw<ArgumentException>()
            .WithMessage("*32 bytes*");
    }

    // ── Null handling: empty string passes through ──
    [Fact]
    public void Encrypt_EmptyString_ReturnsEmpty()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);
        var encrypt = converter.ConvertToProviderExpression.Compile();

        string result = encrypt(string.Empty);

        result.Should().BeEmpty();
    }

    // ── Decrypt empty string passes through ──
    [Fact]
    public void Decrypt_EmptyString_ReturnsEmpty()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);
        var decrypt = converter.ConvertFromProviderExpression.Compile();

        string result = decrypt(string.Empty);

        result.Should().BeEmpty();
    }

    // ── Short ciphertext throws ──
    [Fact]
    public void Decrypt_TooShortCiphertext_ThrowsCryptographicException()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);
        var decrypt = converter.ConvertFromProviderExpression.Compile();

        // 28 bytes total is too short for version (1) + nonce (12) + tag (16)
        string shortCipher = Convert.ToBase64String(new byte[28]);

        FluentActions.Invoking(() => decrypt(shortCipher))
            .Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    // ── Null key throws ──
    [Fact]
    public void Constructor_WithNullKey_ThrowsArgumentNullException() =>
        FluentActions.Invoking(() => new EncryptedStringConverter(null!))
            .Should().Throw<ArgumentNullException>();

    // ── Unicode roundtrip ──
    [Fact]
    public void EncryptThenDecrypt_UnicodeText_RoundTripsCorrectly()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);
        var encrypt = converter.ConvertToProviderExpression.Compile();
        var decrypt = converter.ConvertFromProviderExpression.Compile();

        const string unicodeText = "Hola mundo! \ud83c\udf0e \u3053\u3093\u306b\u3061\u306f";
        string encrypted = encrypt(unicodeText);
        string decrypted = decrypt(encrypted);

        decrypted.Should().Be(unicodeText);
    }

    // \u2500\u2500 Single-key constructor stamps version 1 \u2500\u2500
    [Fact]
    public void Encrypt_SingleKeyConstructor_StampsKeyVersionOne()
    {
        byte[] key = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(key);

        string encrypted = converter.ConvertToProviderExpression.Compile()("versioned");

        Convert.FromBase64String(encrypted)[0].Should().Be(1);
    }

    // \u2500\u2500 Key ring roundtrip \u2500\u2500
    [Fact]
    public void EncryptThenDecrypt_KeyRingConstructor_RoundTrips()
    {
        var keyRing = new Dictionary<byte, byte[]>
        {
            [1] = EncryptedStringConverter.GenerateKey(),
            [2] = EncryptedStringConverter.GenerateKey(),
        };
        var converter = new EncryptedStringConverter(keyRing, currentKeyVersion: 2);

        const string plaintext = "ring-roundtrip";
        string encrypted = converter.ConvertToProviderExpression.Compile()(plaintext);
        string decrypted = converter.ConvertFromProviderExpression.Compile()(encrypted);

        decrypted.Should().Be(plaintext);
    }

    // \u2500\u2500 Rotation: old ciphertext stays readable, new writes use the new version \u2500\u2500
    [Fact]
    public void Rotation_OldCiphertextRemainsReadable_AndNewWritesUseNewVersion()
    {
        byte[] keyV1 = EncryptedStringConverter.GenerateKey();
        byte[] keyV2 = EncryptedStringConverter.GenerateKey();

        var beforeRotation = new EncryptedStringConverter(
            new Dictionary<byte, byte[]> { [1] = keyV1 },
            currentKeyVersion: 1);

        const string plaintext = "written-before-rotation";
        string legacyCipher = beforeRotation.ConvertToProviderExpression.Compile()(plaintext);
        Convert.FromBase64String(legacyCipher)[0].Should().Be(1);

        var afterRotation = new EncryptedStringConverter(
            new Dictionary<byte, byte[]> { [1] = keyV1, [2] = keyV2 },
            currentKeyVersion: 2);
        var encryptAfter = afterRotation.ConvertToProviderExpression.Compile();
        var decryptAfter = afterRotation.ConvertFromProviderExpression.Compile();

        decryptAfter(legacyCipher).Should().Be(plaintext);

        const string freshText = "written-after-rotation";
        string freshCipher = encryptAfter(freshText);

        Convert.FromBase64String(freshCipher)[0].Should().Be(2);
        decryptAfter(freshCipher).Should().Be(freshText);
    }

    // \u2500\u2500 Unknown key version \u2500\u2500
    [Fact]
    public void Decrypt_KeyVersionNotInRing_ThrowsCryptographicException()
    {
        byte[] keyV1 = EncryptedStringConverter.GenerateKey();

        var writer = new EncryptedStringConverter(
            new Dictionary<byte, byte[]> { [1] = keyV1 },
            currentKeyVersion: 1);
        string encrypted = writer.ConvertToProviderExpression.Compile()("orphaned-version");

        var reader = new EncryptedStringConverter(
            new Dictionary<byte, byte[]> { [2] = EncryptedStringConverter.GenerateKey() },
            currentKeyVersion: 2);
        var decrypt = reader.ConvertFromProviderExpression.Compile();

        FluentActions.Invoking(() => decrypt(encrypted))
            .Should().Throw<System.Security.Cryptography.CryptographicException>()
            .WithMessage("*key version 1*");
    }

    // \u2500\u2500 Version byte is authenticated (AAD), so retagging it fails even under the same key \u2500\u2500
    [Fact]
    public void Decrypt_TamperedVersionByte_ThrowsCryptographicException()
    {
        byte[] sharedKey = EncryptedStringConverter.GenerateKey();
        var converter = new EncryptedStringConverter(
            new Dictionary<byte, byte[]> { [1] = sharedKey, [2] = sharedKey },
            currentKeyVersion: 1);
        var decrypt = converter.ConvertFromProviderExpression.Compile();

        byte[] stored = Convert.FromBase64String(
            converter.ConvertToProviderExpression.Compile()("tamper-me"));
        stored[0] = 2;

        FluentActions.Invoking(() => decrypt(Convert.ToBase64String(stored)))
            .Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    // \u2500\u2500 Ring validation: null ring \u2500\u2500
    [Fact]
    public void Constructor_WithNullKeyRing_ThrowsArgumentNullException() =>
        FluentActions.Invoking(() => new EncryptedStringConverter(null!, currentKeyVersion: 1))
            .Should().Throw<ArgumentNullException>();

    // \u2500\u2500 Ring validation: empty ring \u2500\u2500
    [Fact]
    public void Constructor_WithEmptyKeyRing_ThrowsArgumentException() =>
        FluentActions.Invoking(() => new EncryptedStringConverter(
                new Dictionary<byte, byte[]>(),
                currentKeyVersion: 1))
            .Should().Throw<ArgumentException>()
            .WithMessage("*at least one key*");

    // \u2500\u2500 Ring validation: current version missing \u2500\u2500
    [Fact]
    public void Constructor_WithCurrentVersionMissingFromRing_ThrowsArgumentException() =>
        FluentActions.Invoking(() => new EncryptedStringConverter(
                new Dictionary<byte, byte[]> { [1] = EncryptedStringConverter.GenerateKey() },
                currentKeyVersion: 3))
            .Should().Throw<ArgumentException>()
            .WithMessage("*current key version 3*");

    // \u2500\u2500 Ring validation: wrong key length \u2500\u2500
    [Fact]
    public void Constructor_WithKeyRingEntryOfInvalidLength_ThrowsArgumentException() =>
        FluentActions.Invoking(() => new EncryptedStringConverter(
                new Dictionary<byte, byte[]>
                {
                    [1] = EncryptedStringConverter.GenerateKey(),
                    [2] = new byte[16],
                },
                currentKeyVersion: 1))
            .Should().Throw<ArgumentException>()
            .WithMessage("*32 bytes*");

    // \u2500\u2500 Defensive copy: mutating the caller's dictionary does not affect the converter \u2500\u2500
    [Fact]
    public void Constructor_KeyRingIsCopiedDefensively_CallerMutationHasNoEffect()
    {
        byte[] keyV1 = EncryptedStringConverter.GenerateKey();
        var keyRing = new Dictionary<byte, byte[]> { [1] = keyV1 };
        var converter = new EncryptedStringConverter(keyRing, currentKeyVersion: 1);

        const string plaintext = "immune-to-mutation";
        string encrypted = converter.ConvertToProviderExpression.Compile()(plaintext);

        keyRing[1] = EncryptedStringConverter.GenerateKey();
        keyRing[2] = EncryptedStringConverter.GenerateKey();
        keyRing.Remove(1);

        string decrypted = converter.ConvertFromProviderExpression.Compile()(encrypted);

        decrypted.Should().Be(plaintext);
    }
}
