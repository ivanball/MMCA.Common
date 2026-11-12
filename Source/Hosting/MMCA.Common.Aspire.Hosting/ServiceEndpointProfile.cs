namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// The endpoint profile a service project serves on its cleartext endpoint, which picks the startup
/// probe <c>WithMmcaServiceInfrastructure</c> registers (ADR-012).
/// </summary>
public enum ServiceEndpointProfile
{
    /// <summary>
    /// The cleartext endpoint is h2c only (Http2): the probe is <c>WithH2cHealthCheck</c>, since a
    /// stock HTTP/1.1 probe is answered with GOAWAY and never turns healthy.
    /// </summary>
    Http2Only = 0,

    /// <summary>
    /// The ADR-012 mixed profile (the default endpoint serves Http1AndHttp2): the probe is Aspire's
    /// stock <c>WithHttpHealthCheck</c>.
    /// </summary>
    Mixed = 1,
}
