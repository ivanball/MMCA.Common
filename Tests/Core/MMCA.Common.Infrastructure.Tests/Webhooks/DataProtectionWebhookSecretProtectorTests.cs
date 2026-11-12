using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Infrastructure.Webhooks;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <c>DataProtectionWebhookSecretProtector</c> (design 35.14 and 35.16): the signing secret is stored
/// only in its protected form, under the purpose <c>MMCA.Common.Webhooks.Secret.v1</c>, and a value
/// that cannot be unprotected (tampered with, or written under another key ring) is a failed
/// <see cref="MMCA.Common.Shared.Abstractions.Result{T}"/>, never an exception. Built over an
/// ephemeral key ring, so nothing is written to disk.
/// </summary>
public sealed class DataProtectionWebhookSecretProtectorTests
{
    private const string Purpose = "MMCA.Common.Webhooks.Secret.v1";

    [Fact]
    public void RoundTrips()
    {
        var protector = Create(new EphemeralDataProtectionProvider());
        var secret = WebhookSignature.GenerateSecret();

        var stored = protector.Protect(secret);
        var read = protector.Unprotect(stored);

        stored.Should().NotBe(secret).And.NotContain(secret[WebhookSignature.SecretPrefix.Length..], "only the protected form reaches the table");
        read.IsSuccess.Should().BeTrue();
        read.Value.Should().Be(secret);
    }

    [Fact]
    public void UsesThePurposeString()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = Create(provider);
        var secret = WebhookSignature.GenerateSecret();

        var stored = protector.Protect(secret);

        provider.CreateProtector(Purpose).Unprotect(stored).Should().Be(
            secret, "the purpose is part of the contract: a host reading these rows with another purpose could not");
    }

    [Fact]
    public void TamperedValue_Fails()
    {
        var protector = Create(new EphemeralDataProtectionProvider());
        var stored = protector.Protect(WebhookSignature.GenerateSecret());
        var middle = stored.Length / 2;
        var tampered = string.Concat(stored.AsSpan(0, middle), stored[middle] == 'A' ? "B" : "A", stored.AsSpan(middle + 1));

        var act = () => protector.Unprotect(tampered);

        var result = act.Should().NotThrow("Unprotect returns a failure, never throws (rule 35.16)").Subject;
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(WebhookErrorCodes.SecretUnreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a protected payload")]
    [InlineData("whsec_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Garbage_Fails(string stored)
    {
        var protector = Create(new EphemeralDataProtectionProvider());

        var act = () => protector.Unprotect(stored);

        act.Should().NotThrow().Subject.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AnotherKeyRing_Fails()
    {
        // The multi-replica failure mode ADR-069 guards against: a replica without the shared key ring.
        var writer = Create(new EphemeralDataProtectionProvider());
        var reader = Create(new EphemeralDataProtectionProvider());
        var stored = writer.Protect(WebhookSignature.GenerateSecret());

        var act = () => reader.Unprotect(stored);

        act.Should().NotThrow().Subject.IsFailure.Should().BeTrue();
    }

    private static DataProtectionWebhookSecretProtector Create(IDataProtectionProvider provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(provider);
        using var serviceProvider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<DataProtectionWebhookSecretProtector>(serviceProvider);
    }
}
