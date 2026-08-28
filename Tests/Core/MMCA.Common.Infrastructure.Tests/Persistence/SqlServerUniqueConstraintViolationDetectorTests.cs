using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Covers both halves of <see cref="SqlServerUniqueConstraintViolationDetector"/>: the provider
/// error numbers, which are the authoritative answer, and the message fallback that carries the
/// links in the chain that are not a <see cref="SqlException"/>. The number cases build a real
/// <see cref="SqlException"/> through the provider's own non-public factory, because the type has
/// no public constructor and cannot otherwise be produced without a server; if a provider upgrade
/// ever moves that factory the cases report themselves as skipped instead of failing the build for
/// a reason that has nothing to do with the detector.
/// </summary>
public sealed class SqlServerUniqueConstraintViolationDetectorTests
{
    private readonly SqlServerUniqueConstraintViolationDetector _sut = new();

    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void IsUniqueConstraintViolation_WithAUniqueViolationNumber_ReturnsTrue(int number)
    {
        var exception = TryCreateSqlException(number, "Cannot insert duplicate key row in object 'dbo.Widgets'.");
        if (exception is null)
        {
            Assert.Skip("Microsoft.Data.SqlClient no longer exposes the non-public SqlException factory.");
            return;
        }

        _sut.IsUniqueConstraintViolation(exception).Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithAUniqueViolationNumberWrappedByEfCore_ReturnsTrue()
    {
        // The shape EF Core produces: DbUpdateException wrapping the provider's own error. The
        // message on the wrapper says nothing about a duplicate, so only the number can answer.
        var inner = TryCreateSqlException(2627, "Violation of UNIQUE KEY constraint 'UX_Widgets_Code'.");
        if (inner is null)
        {
            Assert.Skip("Microsoft.Data.SqlClient no longer exposes the non-public SqlException factory.");
            return;
        }

        var exception = new InvalidOperationException("An error occurred while saving the entity changes.", inner);

        _sut.IsUniqueConstraintViolation(exception).Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithAnUnrelatedSqlErrorNumber_ReturnsFalse()
    {
        // 547 is the foreign-key violation: a real fault that must keep propagating.
        var exception = TryCreateSqlException(
            547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint 'FK_Widgets_Owners'.");
        if (exception is null)
        {
            Assert.Skip("Microsoft.Data.SqlClient no longer exposes the non-public SqlException factory.");
            return;
        }

        _sut.IsUniqueConstraintViolation(exception).Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithDuplicateKeyOnTheOuterException_ReturnsTrue()
    {
        var exception = new InvalidOperationException(
            "Cannot insert duplicate key row in object 'dbo.Widgets'.");

        _sut.IsUniqueConstraintViolation(exception).Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithDuplicateKeyDeepInTheChain_ReturnsTrue()
    {
        var exception = new InvalidOperationException(
            "Outer",
            new InvalidOperationException(
                "Middle",
                new InvalidOperationException("Cannot insert duplicate key.")));

        _sut.IsUniqueConstraintViolation(exception).Should().BeTrue();
    }

    [Fact]
    public void IsUniqueConstraintViolation_IgnoresCasing_ReturnsTrue() =>
        _sut.IsUniqueConstraintViolation(new InvalidOperationException("Cannot insert DUPLICATE KEY row."))
            .Should().BeTrue();

    [Fact]
    public void IsUniqueConstraintViolation_WithTheSqliteWording_ReturnsTrue() =>
        _sut.IsUniqueConstraintViolation(
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: Widgets.Code'."))
            .Should().BeTrue();

    [Fact]
    public void IsUniqueConstraintViolation_WithAnUnrelatedFailure_ReturnsFalse()
    {
        var exception = new InvalidOperationException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException(
                "The INSERT statement conflicted with the FOREIGN KEY constraint 'FK_Widgets_Owners'."));

        _sut.IsUniqueConstraintViolation(exception).Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintViolation_WithAMessageMerelyQuotingTheErrorNumbers_ReturnsFalse()
    {
        // The numbers are never matched as text: they appear only in SqlException.Number, so a
        // message that happens to contain those digits must not be mistaken for a collision.
        var exception = new InvalidOperationException(
            "Command timed out after 2601 milliseconds (correlation 2627).");

        _sut.IsUniqueConstraintViolation(exception).Should().BeFalse();
    }

    /// <summary>
    /// Builds a genuine <see cref="SqlException"/> carrying <paramref name="number"/> through the
    /// provider's non-public <c>CreateException</c> factory, or <see langword="null"/> when that
    /// factory is no longer reachable.
    /// </summary>
    private static SqlException? TryCreateSqlException(int number, string message)
    {
        try
        {
            var errorConstructor = typeof(SqlError)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length >= 7
                        && parameters[0].ParameterType == typeof(int)
                        && parameters[1].ParameterType == typeof(byte)
                        && parameters[2].ParameterType == typeof(byte)
                        && parameters[3].ParameterType == typeof(string)
                        && parameters[4].ParameterType == typeof(string)
                        && parameters[5].ParameterType == typeof(string)
                        && parameters[6].ParameterType == typeof(int);
                });

            var collectionConstructor = typeof(SqlErrorCollection)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.GetParameters().Length == 0);

            var addMethod = typeof(SqlErrorCollection)
                .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(SqlError)]);

            var createException = typeof(SqlException)
                .GetMethod(
                    "CreateException",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    [typeof(SqlErrorCollection), typeof(string)]);

            if (errorConstructor is null || collectionConstructor is null || addMethod is null || createException is null)
                return null;

            var arguments = new object?[errorConstructor.GetParameters().Length];
            arguments[0] = number;
            arguments[1] = (byte)0;
            arguments[2] = (byte)14;
            arguments[3] = "server";
            arguments[4] = message;
            arguments[5] = "procedure";
            arguments[6] = 0;

            for (var index = 7; index < arguments.Length; index++)
            {
                var parameter = errorConstructor.GetParameters()[index];
                arguments[index] = parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
            }

            var error = (SqlError)errorConstructor.Invoke(arguments);
            var collection = collectionConstructor.Invoke(null);
            addMethod.Invoke(collection, [error]);

            return createException.Invoke(null, [collection, "16.0.0"]) as SqlException;
        }
        catch (Exception exception) when (exception is TargetInvocationException or MissingMethodException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }
}
