namespace MMCA.Common.Gcp.CloudSql;

/// <summary>
/// Cloud SQL IAM database authentication settings, bound from <c>CloudSql:IamAuthentication</c>.
/// <see cref="Enabled"/> is the only key: the database user is the connection string's
/// <c>Username</c>, and the identity whose token is sent is whatever Application Default Credentials
/// resolve to (the workload identity on GKE or Cloud Run, the attached service account on Compute
/// Engine, <c>GOOGLE_APPLICATION_CREDENTIALS</c> elsewhere).
/// </summary>
internal sealed class CloudSqlIamAuthenticationSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionPath = "CloudSql:IamAuthentication";

    /// <summary>Switches PostgreSQL connections onto Cloud SQL IAM tokens. Off when absent.</summary>
    public bool Enabled { get; init; }
}
