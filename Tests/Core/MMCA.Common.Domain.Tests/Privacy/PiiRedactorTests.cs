using AwesomeAssertions;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Privacy;

namespace MMCA.Common.Domain.Tests.Privacy;

/// <summary>
/// Tests for <see cref="PiiRedactor"/> — the logging/telemetry redaction half of the
/// <see cref="PiiAttribute"/> contract: <see cref="PiiAttribute"/>-marked members are masked while
/// non-PII members pass through, and a data subject's personal values never appear in the output.
/// </summary>
public sealed class PiiRedactorTests
{
    private sealed class Subject
    {
        public int Id { get; init; } = 42;

        [Pii]
        public string Email { get; init; } = "jane@example.com";

        [Pii]
        public string FullName { get; init; } = "Jane Roe";

        public string City { get; init; } = "Atlanta";
    }

    private sealed class NoPii
    {
        public string Name { get; init; } = "public-content";
    }

    [Fact]
    public void Redact_MasksPiiProperties_AndPassesThroughNonPii()
    {
        var map = PiiRedactor.Redact(new Subject());

        map["Id"].Should().Be(42);
        map["City"].Should().Be("Atlanta");
        map["Email"].Should().Be(PiiRedactor.RedactedToken);
        map["FullName"].Should().Be(PiiRedactor.RedactedToken);
    }

    [Fact]
    public void Redact_NeverEmitsTheClearTextPiiValues()
    {
        var map = PiiRedactor.Redact(new Subject());

        map.Values.Should().NotContain("jane@example.com").And.NotContain("Jane Roe");
    }

    [Fact]
    public void Redact_Null_ReturnsEmptyMap() =>
        PiiRedactor.Redact(null).Should().BeEmpty();

    [Fact]
    public void RedactToString_MasksPii_AndKeepsTypeAndScalars()
    {
        var text = PiiRedactor.RedactToString(new Subject());

        text.Should().StartWith("Subject {");
        text.Should().Contain("Id = 42").And.Contain("City = Atlanta");
        text.Should().Contain("Email = [REDACTED]").And.Contain("FullName = [REDACTED]");
        text.Should().NotContain("jane@example.com").And.NotContain("Jane Roe");
    }

    [Fact]
    public void RedactToString_Null_ReturnsNullLiteral() =>
        PiiRedactor.RedactToString(null).Should().Be("null");

    [Fact]
    public void HasPii_IsTrue_WhenTypeDeclaresPiiProperty() =>
        PiiRedactor.HasPii(typeof(Subject)).Should().BeTrue();

    [Fact]
    public void HasPii_IsFalse_WhenTypeHasNoPiiProperty() =>
        PiiRedactor.HasPii(typeof(NoPii)).Should().BeFalse();
}
