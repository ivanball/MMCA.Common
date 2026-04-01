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

        // 10 bytes total is too short for nonce (12) + tag (16)
        string shortCipher = Convert.ToBase64String(new byte[10]);

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
}
