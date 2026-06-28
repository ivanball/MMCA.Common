using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Privacy;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Non-vacuous §30 fitness function for the full PII data-governance contract (ADR-005). The shared
/// <see cref="MMCA.Common.Testing.Architecture.PiiConventionTestsBase"/> scan (<c>[Pii] ⇒ IAnonymizable</c>)
/// is structurally vacuous in the framework — no data-subject type lives in MMCA.Common's Domain — so
/// this test closes that gap by <em>forcing a representative <c>[Pii]</c>-carrying data subject through
/// the framework's own machinery</em>: it must be (1) recognised by <see cref="PiiRedactor"/>,
/// (2) masked with no clear-text leak in logs/telemetry, and (3) erasable in place via
/// <see cref="IAnonymizable"/>. The three §30 mechanisms are thereby proven to compose end to end,
/// rather than each being verified in isolation. Consumers (e.g. MMCA.ADC's <c>User</c>) exercise the
/// same contract against their real PII-bearing aggregates.
/// </summary>
public sealed class PiiErasureContractFitnessTests
{
    [Fact]
    public void DataSubject_DeclaresPii_SoTheContractIsNotVacuous() =>
        PiiRedactor.HasPii(typeof(DataSubjectSample)).Should().BeTrue(
            "a representative data subject must declare [Pii] members, or the redaction guard would assert nothing");

    [Fact]
    public void PiiRedactor_MasksEveryPiiMember_AndPassesThroughNonPii()
    {
        var redacted = PiiRedactor.Redact(new DataSubjectSample());

        redacted[nameof(DataSubjectSample.Email)].Should().Be(PiiRedactor.RedactedToken);
        redacted[nameof(DataSubjectSample.FullName)].Should().Be(PiiRedactor.RedactedToken);
        redacted[nameof(DataSubjectSample.Id)].Should().Be(DataSubjectSample.SampleId);
        redacted[nameof(DataSubjectSample.City)].Should().Be(DataSubjectSample.PublicCity);
    }

    [Fact]
    public void PiiRedactor_LeaksNoClearTextPii_ToLogsOrTelemetry()
    {
        var sample = new DataSubjectSample();

        PiiRedactor.Redact(sample).Values
            .Should().NotContain(DataSubjectSample.OriginalEmail)
            .And.NotContain(DataSubjectSample.OriginalFullName);

        var rendered = PiiRedactor.RedactToString(sample);
        rendered.Should().Contain(PiiRedactor.RedactedToken);
        rendered.Should().NotContain(DataSubjectSample.OriginalEmail);
        rendered.Should().NotContain(DataSubjectSample.OriginalFullName);
    }

    [Fact]
    public void DataSubject_ImplementsErasureSeam_AndAnonymizeErasesPii_Idempotently()
    {
        var sample = new DataSubjectSample();
        sample.Should().BeAssignableTo<IAnonymizable>(
            "a [Pii]-carrying data subject must expose a right-to-erasure path (ADR-005)");

        var first = sample.Anonymize();
        first.IsSuccess.Should().BeTrue();
        sample.Email.Should().NotBe(DataSubjectSample.OriginalEmail);
        sample.FullName.Should().NotBe(DataSubjectSample.OriginalFullName);

        var second = sample.Anonymize();
        second.IsSuccess.Should().BeTrue("Anonymize must be idempotent (ADR-005)");
        sample.Email.Should().NotBe(DataSubjectSample.OriginalEmail);

        // Erasure and redaction compose: an anonymized subject still leaks no original clear text.
        PiiRedactor.RedactToString(sample)
            .Should().NotContain(DataSubjectSample.OriginalEmail)
            .And.NotContain(DataSubjectSample.OriginalFullName);
    }

    /// <summary>
    /// A representative data subject: a non-PII identifier and public field alongside <c>[Pii]</c>
    /// members, with an idempotent in-place erasure path. Models the shape every consumer PII aggregate
    /// (e.g. an account holder) must satisfy, without inventing a fake aggregate in the framework Domain.
    /// </summary>
    private sealed class DataSubjectSample : IAnonymizable
    {
        public const int SampleId = 7;
        public const string PublicCity = "Atlanta";
        public const string OriginalEmail = "jane.doe@example.com";
        public const string OriginalFullName = "Jane Doe";

        private const string AnonymizedEmail = "anonymized@example.invalid";
        private const string AnonymizedName = "[anonymized]";

        public int Id { get; init; } = SampleId;

        [Pii]
        public string Email { get; private set; } = OriginalEmail;

        [Pii]
        public string FullName { get; private set; } = OriginalFullName;

        public string City { get; init; } = PublicCity;

        public Result Anonymize()
        {
            // Idempotent by construction: re-applying the fixed placeholders is a no-op success.
            Email = AnonymizedEmail;
            FullName = AnonymizedName;
            return Result.Success();
        }
    }
}
