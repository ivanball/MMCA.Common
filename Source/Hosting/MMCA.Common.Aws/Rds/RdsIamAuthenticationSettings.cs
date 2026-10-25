namespace MMCA.Common.Aws.Rds;

/// <summary>
/// RDS IAM database authentication settings, bound from <c>Rds:IamAuthentication</c>.
/// <see cref="Enabled"/> is the gate. Host, port and user come from each PostgreSQL connection string,
/// so nothing about the database itself is configured here.
/// </summary>
internal sealed class RdsIamAuthenticationSettings
{
    /// <summary>The configuration section path.</summary>
    internal const string SectionPath = "Rds:IamAuthentication";

    /// <summary>Switches PostgreSQL connections onto RDS IAM tokens. Off when absent.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The region the token is signed for (system name, e.g. <c>us-east-1</c>): the database's region.
    /// When blank the AWS SDK resolves it from its default chain (<c>AWS_REGION</c>, the shared profile,
    /// instance metadata).
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Optional static access key, set together with <see cref="SecretAccessKey"/>; when both are blank
    /// the AWS default credential chain applies (an IAM role, the environment, the shared profile).
    /// Setting only one of the two fails registration.
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>The secret paired with <see cref="AccessKeyId"/>.</summary>
    public string? SecretAccessKey { get; init; }
}
