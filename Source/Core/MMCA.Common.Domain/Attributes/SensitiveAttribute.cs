namespace MMCA.Common.Domain.Attributes;

/// <summary>
/// Marks a property as a secret or credential (a hash, a salt, an API key, a signing secret) whose
/// value must never be written in readable form to a persisted history such as the audit trail.
/// <para>
/// This is deliberately NOT a personal-data marker. A <see cref="SensitiveAttribute"/> property is
/// not data-subject PII, so it does not feed the mechanisms <see cref="PiiAttribute"/> drives: it
/// does not make <c>PiiRedactor.HasPii</c> true, it is not masked by <c>PiiRedactor.Redact</c>, and
/// it does not oblige the entity to implement <c>IAnonymizable</c>. The audit trail
/// (<c>AuditTrailSaveChangesInterceptor</c>) records the redaction placeholder in place of its old
/// and new values.
/// </para>
/// <para>
/// The password hash and salt of an entity implementing <c>IAuthUser</c> are treated this way
/// without any marker; apply this attribute to other secrets the framework cannot recognize.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class SensitiveAttribute : Attribute;
