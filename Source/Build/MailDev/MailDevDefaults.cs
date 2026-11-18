namespace MMCA.Common.MailDev;

/// <summary>
/// The fixed host ports of the local MailDev container, in one place for the two packages that must
/// agree on them: <c>MMCA.Common.Aspire.Hosting</c> publishes MailDev on these ports
/// (<c>AddMailDev</c>, re-exposed as <c>Extensions.MailDevHttpPort</c> and
/// <c>Extensions.MailDevSmtpPort</c>), and <c>MMCA.Common.Testing.E2E</c> reads the captured inbox
/// from the web port (<c>MailDevInbox</c>).
/// </summary>
/// <remarks>
/// One source file, compiled as an internal type into both packages through a
/// <c>Compile Include ... Link</c> in each project file (the pattern of
/// <c>Source/Build/RateLimiting</c>). A shared reference would make the E2E package depend on the
/// Aspire hosting SDK, or the hosting package on Playwright, for two integers; a linked file keeps
/// both packages free of the other while a port move still lands in one edit.
/// </remarks>
internal static class MailDevDefaults
{
    /// <summary>The host port MailDev's web UI and REST API are published on.</summary>
    public const int HttpPort = 1080;

    /// <summary>The host port MailDev's SMTP listener is published on.</summary>
    public const int SmtpPort = 1025;
}
