using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="ProfilingHelper"/> verifying that wrapped operations execute
/// correctly when MiniProfiler is not active (production default).
/// </summary>
public sealed class ProfilingHelperTests
{
    // ── BeginStep ──
    [Fact]
    public void BeginStep_WhenNoProfilerActive_ReturnsNull()
    {
        var result = ProfilingHelper.BeginStep("TestClass", "TestMethod");

        result.Should().BeNull();
    }

    // ── Profile (sync) ──
    [Fact]
    public void Profile_ExecutesFuncAndReturnsResult()
    {
        int result = ProfilingHelper.Profile("TestClass", "TestMethod", () => 42);

        result.Should().Be(42);
    }

    [Fact]
    public void Profile_ExecutesFuncExactlyOnce()
    {
        int callCount = 0;

        ProfilingHelper.Profile("TestClass", "TestMethod", () =>
        {
            callCount++;
            return 1;
        });

        callCount.Should().Be(1);
    }

    // ── ProfileAsync (void) ──
    [Fact]
    public async Task ProfileAsync_Void_ExecutesFunc()
    {
        bool executed = false;

        await ProfilingHelper.ProfileAsync("TestClass", "TestMethod", () =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        executed.Should().BeTrue();
    }

    [Fact]
    public async Task ProfileAsync_Void_PropagatesException()
    {
        Func<Task> act = () => ProfilingHelper.ProfileAsync("TestClass", "TestMethod", () =>
            throw new InvalidOperationException("test error"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("test error");
    }

    // ── ProfileAsync<T> (with return value) ──
    [Fact]
    public async Task ProfileAsync_WithReturn_ExecutesAndReturnsResult()
    {
        string result = await ProfilingHelper.ProfileAsync("TestClass", "TestMethod", () =>
            Task.FromResult("hello"));

        result.Should().Be("hello");
    }

    [Fact]
    public async Task ProfileAsync_WithReturn_PropagatesException()
    {
        Func<Task> act = () => ProfilingHelper.ProfileAsync<int>("TestClass", "TestMethod", () =>
            throw new InvalidOperationException("async error"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("async error");
    }

    [Fact]
    public async Task ProfileAsync_WithReturn_ExecutesFuncExactlyOnce()
    {
        int callCount = 0;

        await ProfilingHelper.ProfileAsync("TestClass", "TestMethod", () =>
        {
            callCount++;
            return Task.FromResult(99);
        });

        callCount.Should().Be(1);
    }
}
