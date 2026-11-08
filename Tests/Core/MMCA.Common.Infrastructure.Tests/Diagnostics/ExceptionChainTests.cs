using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Diagnostics;
using MMCA.Common.Infrastructure.Persistence.Conversions;

namespace MMCA.Common.Infrastructure.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="ExceptionChain"/>: the text a background-job failure records in its
/// <c>LastError</c> column, its log line and its span status. The outer message alone hid the real
/// cause (an SMTP reply on <see cref="Exception.InnerException"/>), so the chain is written out.
/// </summary>
public sealed class ExceptionChainTests
{
    [Fact]
    public void Describe_NoInnerException_IsTheBareMessage()
    {
        var text = ExceptionChain.Describe(new InvalidOperationException("Dispatch failed"));

        text.Should().Be("Dispatch failed", "an exception with no cause renders exactly as before");
    }

    [Fact]
    public void Describe_OneInnerException_AppendsItsTypeAndMessage()
    {
        var exception = new InvalidOperationException("a", new IOException("b"));

        ExceptionChain.Describe(exception).Should().Be("a ---> IOException: b");
    }

    [Fact]
    public void Describe_NestedInnerExceptions_AppendsEachLevelInOrder()
    {
        var exception = new InvalidOperationException(
            "Failure sending mail.",
            new IOException("Unable to read data from the transport connection.", new TimeoutException("550 5.1.1 mailbox unavailable")));

        ExceptionChain.Describe(exception).Should().Be(
            "Failure sending mail. ---> IOException: Unable to read data from the transport connection. ---> TimeoutException: 550 5.1.1 mailbox unavailable");
    }

    [Fact]
    public void Describe_AggregateException_ListsEachInnerException()
    {
        var exception = new AggregateException(
            "batch failed",
            new InvalidOperationException("first"),
            new ArgumentException("second"));

        var text = ExceptionChain.Describe(exception);

        text.Should().StartWith("batch failed");
        text.Should().Contain(" ---> InvalidOperationException: first");
        text.Should().Contain(" ---> ArgumentException: second");
        text.IndexOf("first", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("second", StringComparison.Ordinal), "inner exceptions keep their order");
    }

    [Fact]
    public void Describe_AggregateWrappedInAnOuterException_LooksInsideTheAggregate()
    {
        var exception = new InvalidOperationException(
            "outer",
            new AggregateException("several", new IOException("the real cause")));

        ExceptionChain.Describe(exception).Should().Contain(" ---> IOException: the real cause");
    }

    [Fact]
    public void Describe_ALevelRepeatingThePreviousMessage_IsSkipped()
    {
        var exception = new InvalidOperationException("same", new InvalidOperationException("same", new IOException("cause")));

        ExceptionChain.Describe(exception).Should().Be("same ---> IOException: cause");
    }

    [Fact]
    public void Describe_ADeeperChainThanEightLevels_StopsAfterEight()
    {
        Exception exception = new IOException("level 9");
        for (var level = 8; level >= 1; level--)
        {
            exception = new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"level {level}"), exception);
        }

        var text = ExceptionChain.Describe(exception);

        text.Should().Contain("level 8");
        text.Should().NotContain("level 9", "the chain is cut after eight levels");
    }

    [Fact]
    public void Describe_NeverIncludesAStackTrace()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("outer", new IOException("inner"));
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        var text = ExceptionChain.Describe(thrown);

        text.Should().NotContain(" at ").And.NotContain(Environment.NewLine);
    }

    [Fact]
    public void Describe_ALongChainCutToTheColumnWidth_KeepsTheOuterMessageFirst()
    {
        var exception = new InvalidOperationException("outer", new IOException(new string('x', 5000)));

        var stored = ColumnWidth.Truncate(ExceptionChain.Describe(exception), 4000);

        stored.Should().HaveLength(4000);
        stored.Should().StartWith("outer ---> IOException: x");
    }
}
