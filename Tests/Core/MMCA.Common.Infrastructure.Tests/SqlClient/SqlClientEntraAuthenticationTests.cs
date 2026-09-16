using Microsoft.Data.SqlClient;

namespace MMCA.Common.Infrastructure.Tests.SqlClient;

/// <summary>
/// Pins the <c>Microsoft.Data.SqlClient.Extensions.Azure</c> reference that <c>MMCA.Common.Infrastructure</c>
/// carries beside its SqlClient 7 pin. Since SqlClient 7.0 the Entra ID authentication providers are no
/// longer in the core driver, and a host whose connection string says
/// <c>Authentication=Active Directory Managed Identity</c> fails at startup with
/// "Cannot find an authentication provider for 'ActiveDirectoryManagedIdentity'" when the extension is
/// missing, which is exactly what took the ADC production revisions down on 2026-09-16. The extension
/// registers its providers when its assembly loads, so touching one of its types is enough here.
/// </summary>
public sealed class SqlClientEntraAuthenticationTests
{
    [Fact]
    public void ManagedIdentityAuthenticationProvider_IsRegistered()
    {
        // Loads the extension assembly the same way the driver does on first use.
        _ = typeof(ActiveDirectoryAuthenticationProvider);

        var provider = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity);

        Assert.NotNull(provider);
    }
}
