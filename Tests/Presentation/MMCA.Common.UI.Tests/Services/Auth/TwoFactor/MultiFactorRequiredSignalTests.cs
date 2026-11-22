using AwesomeAssertions;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Auth.TwoFactor;

namespace MMCA.Common.UI.Tests.Services.Auth.TwoFactor;

/// <summary>
/// The in-process "second factor required" signal (design 2.2.3): keyed by the page scope's token
/// storage, so a raise reaches only that scope's subscriber, once per raise; disposing a subscription
/// stops it, and a replaced subscription leaves its successor in place.
/// Each test uses its own storage instance, so tests running alongside never share a key.
/// </summary>
public sealed class MultiFactorRequiredSignalTests
{
    [Fact]
    public void ARaise_InvokesTheSubscriberOfThatStorage_OncePerRaise()
    {
        var storage = new StubTokenStorageService();
        var calls = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => calls++);

        MultiFactorRequiredSignal.Raise(storage);
        MultiFactorRequiredSignal.Raise(storage);

        calls.Should().Be(2, "each refusal is one raise and one callback");
    }

    [Fact]
    public void ARaiseForAnotherStorage_DoesNotReachThisSubscriber()
    {
        var mine = new StubTokenStorageService();
        var other = new StubTokenStorageService();
        var calls = 0;
        using var subscription = MultiFactorRequiredSignal.Subscribe(mine, () => calls++);

        MultiFactorRequiredSignal.Raise(other);

        calls.Should().Be(0, "a refusal belongs to the scope whose storage attached the bearer");
        MultiFactorRequiredSignal.HasSubscriber(other).Should().BeFalse();
    }

    [Fact]
    public void HasSubscriber_IsTrueWhileSubscribed_AndFalseAfterDispose()
    {
        var storage = new StubTokenStorageService();
        var calls = 0;
        var subscription = MultiFactorRequiredSignal.Subscribe(storage, () => calls++);

        MultiFactorRequiredSignal.HasSubscriber(storage).Should().BeTrue();

        subscription.Dispose();
        MultiFactorRequiredSignal.Raise(storage);

        MultiFactorRequiredSignal.HasSubscriber(storage).Should().BeFalse("a disposed listener no longer asks for 403 bodies to be read");
        calls.Should().Be(0, "a disposed subscription is never invoked");
    }

    [Fact]
    public void DisposingAReplacedSubscription_KeepsItsSuccessor()
    {
        var storage = new StubTokenStorageService();
        var first = 0;
        var second = 0;
        var replaced = MultiFactorRequiredSignal.Subscribe(storage, () => first++);
        using var current = MultiFactorRequiredSignal.Subscribe(storage, () => second++);

        replaced.Dispose();
        MultiFactorRequiredSignal.Raise(storage);

        MultiFactorRequiredSignal.HasSubscriber(storage).Should().BeTrue("the newer listener of the same scope is still subscribed");
        second.Should().Be(1);
        first.Should().Be(0);
    }
}
