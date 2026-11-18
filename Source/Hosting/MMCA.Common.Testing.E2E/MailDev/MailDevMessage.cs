namespace MMCA.Common.Testing.E2E.MailDev;

/// <summary>One message read back from MailDev's REST API by <see cref="MailDevInbox"/>.</summary>
/// <param name="Id">MailDev's message id.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Html">The HTML body, HTML entities decoded (so an <c>&amp;amp;</c> in a link reads as <c>&amp;</c>).</param>
/// <param name="To">The recipient addresses.</param>
public sealed record MailDevMessage(string Id, string Subject, string Html, IReadOnlyList<string> To);
