using MMCA.Common.Infrastructure.Services;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Structural credential-storage rule for <see cref="PasswordHasher"/>, asserted against the compiled IL
/// rather than the source: the type must depend on <c>Rfc2898DeriveBytes</c> (a deliberately slow key
/// derivation function, not a bare fast hash) and on <c>CryptographicOperations</c> (the constant-time
/// comparison that keeps verification off a timing side channel). The known-answer and constant pins in
/// <c>PasswordHasherSecurityTests</c> cover the parameter values; this covers the shape, so a rewrite that
/// swapped PBKDF2 for a raw SHA-512 or replaced the fixed-time compare with <c>SequenceEqual</c> fails the
/// build even if it kept the same output length.
/// </summary>
public sealed class PasswordHashingFitnessTests
{
    private const string CryptographicOperationsType = "System.Security.Cryptography.CryptographicOperations";

    private const string Rfc2898DeriveBytesType = "System.Security.Cryptography.Rfc2898DeriveBytes";

    private static Assembly Infrastructure => typeof(PasswordHasher).Assembly;

    [Fact]
    public void ScannedPasswordHasherSet_IsNotEmpty() =>
        PasswordHasherTypes().GetTypes().Should().ContainSingle(
            t => string.Equals(t.FullName, typeof(PasswordHasher).FullName, StringComparison.Ordinal),
            "a rule that selects nothing passes vacuously: the scan must actually reach PasswordHasher");

    [Fact]
    public void PasswordHasher_DependsOnASlowKeyDerivationFunction()
    {
        var result = PasswordHasherTypes()
            .Should()
            .HaveDependencyOnAll(Rfc2898DeriveBytesType)
            .GetResult();

        ArchitectureAssert.NoViolations(result,
            "credentials must be stretched with PBKDF2; a bare hash lets commodity GPUs try billions of "
            + "candidate passwords per second against a stolen table");
    }

    [Fact]
    public void PasswordHasher_DependsOnConstantTimeComparison()
    {
        var result = PasswordHasherTypes()
            .Should()
            .HaveDependencyOnAll(CryptographicOperationsType)
            .GetResult();

        ArchitectureAssert.NoViolations(result,
            "verification must compare with CryptographicOperations.FixedTimeEquals; a short-circuiting "
            + "comparison leaks the matching prefix length through response timing, recovering a digest "
            + "byte by byte");
    }

    private static PredicateList PasswordHasherTypes() =>
        Types.InAssembly(Infrastructure)
            .That()
            .HaveName(nameof(PasswordHasher));
}
