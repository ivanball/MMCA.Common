namespace MMCA.Common.Domain.Attributes;

/// <summary>
/// Marks a property as personally identifiable information (PII) belonging to a data subject.
/// Two governance mechanisms rely on this marker:
/// <list type="bullet">
///   <item>An architecture fitness test asserts that any entity declaring a <see cref="PiiAttribute"/>
///   property also implements <c>IAnonymizable</c>, so a data subject's personal data always has a
///   right-to-erasure path — reconciling soft-delete with GDPR/CCPA erasure (see ADR-005).</item>
///   <item>A logging destructuring policy masks <see cref="PiiAttribute"/>-marked members so PII does
///   not leak into structured logs or telemetry.</item>
/// </list>
/// Apply only to genuine data-subject PII (e.g. an account holder's email/name), not to public
/// content that merely happens to contain a name (e.g. a conference speaker profile sourced from a
/// public agenda, whose erasure obligation — if any — flows through the linked user account).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PiiAttribute : Attribute;
