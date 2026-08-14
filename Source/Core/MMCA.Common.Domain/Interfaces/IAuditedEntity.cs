namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Opt-in marker: an entity carrying this interface has every insert, update and delete recorded
/// as an immutable change-history trail (who changed which field, from what to what, and when).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a marker rather than a global default.</b> A change trail is one row per changed property
/// per save, so trailing every entity in a busy system multiplies write volume and storage without
/// anyone asking for it. Marking entities one at a time makes the volume a deliberate decision:
/// trail the aggregates whose history someone will actually be asked to produce (an order, a
/// permission grant, a ticket) and leave high-churn bookkeeping tables alone.
/// </para>
/// <para>
/// <b>It composes with <see cref="IAuditableEntity"/> but does not require it.</b> The two answer
/// different questions. <see cref="IAuditableEntity"/> stamps the CURRENT state (who touched this
/// row last, and when); this marker records the SEQUENCE that produced it. An entity may carry
/// either, both, or neither. When both are present the trail rows see the freshly stamped values,
/// because the trail is captured after the stamping interceptor has run.
/// </para>
/// <para>
/// <b>Recording is host-gated.</b> Nothing is written unless the host opted in with
/// <c>AddAuditTrail(configuration)</c> and <c>AuditTrail:Enabled</c>. Marking an entity in a host
/// that never opted in is inert: no table, no rows, no cost.
/// </para>
/// <para>
/// <b>Personal data is redacted at capture.</b> A property marked with
/// <c>MMCA.Common.Domain.Attributes.PiiAttribute</c> records a redaction placeholder on both sides
/// of the change instead of its value, so the trail never becomes a second, unerasable copy of a
/// data subject's personal data (ADR-005).
/// </para>
/// </remarks>
public interface IAuditedEntity;
