using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance.Deployment;

/// <summary>
/// Guard for <see cref="MessageBusBackpressureTestsBase"/>. The subclass reads a fixture
/// <c>appsettings.json</c> embedded in THIS assembly (with comments, a trailing comma, camel-cased keys
/// and a quoted number, all of which the configuration binder accepts), so the inherited fact runs the
/// cross-assembly path a consumer runs. The parse is exercised directly through
/// <see cref="MessageBusBackpressureTestsBase.Check"/>, and the key names the package matches by string
/// are pinned against <see cref="MessageBusSettings"/>, the type the host actually binds.
/// </summary>
public sealed class MessageBusBackpressureTestsBaseTests : MessageBusBackpressureTestsBase
{
    protected override IReadOnlyList<string> Services => ["Fixture"];

    [Fact]
    public void KeyNames_MatchTheSettingsTypeTheHostBinds()
    {
        MessageBusSectionName.Should().Be(MessageBusSettings.SectionName);
        PrefetchCountKey.Should().Be(nameof(MessageBusSettings.PrefetchCount));
        ConcurrentMessageLimitKey.Should().Be(nameof(MessageBusSettings.ConcurrentMessageLimit));
    }

    [Fact]
    public void BoundedSettings_HaveNoViolations() =>
        Check("Svc", """{ "MessageBus": { "PrefetchCount": 16, "ConcurrentMessageLimit": 8 } }""")
            .Should().BeEmpty();

    [Fact]
    public void MissingSection_IsAViolation() =>
        Check("Svc", """{ "Logging": {} }""")
            .Should().ContainSingle().Which.Should().Contain("Svc declares no MessageBus section");

    [Fact]
    public void MissingPrefetch_IsAViolation() =>
        Check("Svc", """{ "MessageBus": { "ConcurrentMessageLimit": 8 } }""")
            .Should().ContainSingle().Which.Should().Contain("PrefetchCount");

    [Fact]
    public void ZeroConcurrency_IsAViolation() =>
        Check("Svc", """{ "MessageBus": { "PrefetchCount": 16, "ConcurrentMessageLimit": 0 } }""")
            .Should().ContainSingle().Which.Should().Contain("ConcurrentMessageLimit");

    [Fact]
    public void PrefetchBelowConcurrency_IsAViolation() =>
        Check("Svc", """{ "MessageBus": { "PrefetchCount": 4, "ConcurrentMessageLimit": 8 } }""")
            .Should().ContainSingle().Which.Should().Contain("prefetches 4 but processes 8");

    [Fact]
    public void EmptyServiceList_FailsRatherThanCheckingNothing()
    {
        var assert = new NoServices().EveryServiceAppSettings_BoundsBrokerBackpressure;

        assert.Should().Throw<Exception>("a gate that lists no service checks nothing");
    }

    private sealed class NoServices : MessageBusBackpressureTestsBase
    {
        protected override IReadOnlyList<string> Services => [];
    }
}
