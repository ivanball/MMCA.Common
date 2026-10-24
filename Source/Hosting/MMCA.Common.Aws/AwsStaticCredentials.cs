using Amazon.Runtime;

namespace MMCA.Common.Aws;

/// <summary>
/// Turns an optional access key pair from configuration into SDK credentials, shared by the storage
/// and the broker registrations so both read a key pair the same way.
/// </summary>
internal static class AwsStaticCredentials
{
    /// <summary>
    /// Returns static credentials when both halves of the pair are set, <see langword="null"/> when
    /// both are blank (the client then resolves the AWS default credential chain: an IAM role, the
    /// environment, the shared profile), and throws when only one half is set, because silently
    /// falling back to the default chain would run the host under an identity nobody configured.
    /// </summary>
    /// <param name="accessKeyId">The configured access key id.</param>
    /// <param name="secretAccessKey">The configured secret access key.</param>
    /// <param name="sectionPath">The configuration section the pair lives in, named in the error.</param>
    /// <returns>The static credentials, or <see langword="null"/> for the default chain.</returns>
    /// <exception cref="InvalidOperationException">Exactly one half of the pair is set.</exception>
    internal static BasicAWSCredentials? Resolve(string? accessKeyId, string? secretAccessKey, string sectionPath)
    {
        var hasKey = !string.IsNullOrWhiteSpace(accessKeyId);
        var hasSecret = !string.IsNullOrWhiteSpace(secretAccessKey);

        if (hasKey && hasSecret)
        {
            return new BasicAWSCredentials(accessKeyId, secretAccessKey);
        }

        if (hasKey || hasSecret)
        {
            throw new InvalidOperationException(
                $"{sectionPath}:AccessKeyId and {sectionPath}:SecretAccessKey must be set together. Set both for static credentials, or neither to use the AWS default credential chain.");
        }

        return null;
    }
}
