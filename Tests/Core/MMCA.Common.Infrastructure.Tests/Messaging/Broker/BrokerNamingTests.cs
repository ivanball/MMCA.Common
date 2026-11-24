using AwesomeAssertions;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Broker;

/// <summary>
/// <see cref="BrokerNaming"/> is the one place a publisher and a consumer in different hosts derive
/// the same broker names, so every rule here is a wire contract.
/// </summary>
public sealed class BrokerNamingTests
{
    public sealed record class UserRegistered : BaseIntegrationEvent;

    [EventName("Identity.UserRegistered.v2")]
    public sealed record class NamedUserRegistered : BaseIntegrationEvent;

    [Theory]
    [InlineData("UserRegistered", "user-registered")]
    [InlineData("MMCA.ADC.Engagement.Service", "mmca-adc-engagement-service")]
    [InlineData("  Order__Placed!!v2  ", "order-placed-v2")]
    [InlineData("already-kebab", "already-kebab")]
    [InlineData("ABC", "abc")]
    [InlineData("---", "")]
    public void Kebab_LowersAndCollapsesEveryRunOutsideLettersAndDigits(string value, string expected) =>
        BrokerNaming.Kebab(value).Should().Be(expected);

    [Fact]
    public void Kebab_Null_Throws()
    {
        var act = () => BrokerNaming.Kebab(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EventName_WithoutADeclaredName_IsTheKebabFullName() =>
        BrokerNaming.EventName(typeof(UserRegistered)).Should().Be(
            "mmca-common-infrastructure-tests-messaging-broker-broker-naming-tests-user-registered",
            "the full name keeps two same-named events in different modules on different topics");

    [Fact]
    public void EventName_WithADeclaredName_UsesIt_SoANamespaceMoveKeepsTheTopic() =>
        BrokerNaming.EventName(typeof(NamedUserRegistered)).Should().Be("identity-user-registered-v2");

    [Fact]
    public void Topic_PrefixesTheKebabApplicationNamespace() =>
        BrokerNaming.Topic("Demo.Orders", "order-placed").Should().Be("demo-orders-order-placed");

    [Theory]
    [InlineData(null, "MMCA.ADC.Engagement.Service", "mmca-adc-engagement")]
    [InlineData("", "Store.Catalog.Service", "store-catalog")]
    [InlineData(null, "Gateway", "gateway")]
    [InlineData(null, "service", "service")]
    [InlineData("Orders.Service", "ignored", "orders-service")]
    public void Queue_ConfiguredNameWins_ElseTheApplicationNameWithoutItsServiceSuffix(
        string? configured, string? applicationName, string expected) =>
        BrokerNaming.Queue(configured, applicationName).Should().Be(expected);

    [Fact]
    public void Queue_NoUsableName_ThrowsNamingTheSetting()
    {
        var act = () => BrokerNaming.Queue(null, "  ");

        act.Should().Throw<InvalidOperationException>().WithMessage("*MessageBus:QueueName*");
    }

    [Fact]
    public void Fit_ANameWithinTheLimit_IsUnchanged() =>
        BrokerNaming.Fit("short-name", 50).Should().Be("short-name");

    [Fact]
    public void Fit_ALongName_IsShortenedDeterministicallyWithAHashSuffix()
    {
        var name = new string('a', 30) + "-" + new string('b', 40);

        var fitted = BrokerNaming.Fit(name, 50);

        fitted.Should().HaveLength(50);
        fitted.Should().MatchRegex("^a+(-b+)?-[0-9a-f]{8}$");
        BrokerNaming.Fit(name, 50).Should().Be(fitted, "every host must compute the same name");
    }

    [Fact]
    public void Fit_TwoLongNamesWithTheSamePrefix_StayDistinct()
    {
        var prefix = new string('x', 60);

        BrokerNaming.Fit(prefix + "-one", 50).Should().NotBe(BrokerNaming.Fit(prefix + "-two", 50));
    }

    [Fact]
    public void Fit_ALimitBelowSixteen_Throws()
    {
        var act = () => BrokerNaming.Fit("name", 15);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
