using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Tests.Fixtures;

namespace MMCA.Common.AI.Tests.Providers;

/// <summary>
/// The <c>provider</c> metric tag is what the inner client says it is. A host that supplies a
/// foreign client through the factory overload is metered as that client's own provider; the
/// configured name only fills in when the client reports none. The measurement itself is asserted
/// in <c>UsageRecordingChatClientTests</c>, against a recorder scoped to one meter factory.
/// </summary>
public sealed class ProviderTagTests
{
    [Fact]
    public void ResolveProviderName_PrefersTheClientsOwnMetadata()
    {
        using var inner = new StubChatClient(service: new ChatClientMetadata("openai", new Uri("https://api.openai.com/v1/"), "gpt-5"));

        UsageRecordingChatClient.ResolveProviderName(inner, "Anthropic").Should().Be("openai");
    }

    [Fact]
    public void ResolveProviderName_FallsBackToTheConfiguredNameLowerCased()
    {
        using var inner = new StubChatClient();

        UsageRecordingChatClient.ResolveProviderName(inner, "MyGateway").Should().Be("mygateway");
    }

    [Fact]
    public void ResolveProviderName_WithNothingToGoOn_IsUnknown()
    {
        using var inner = new StubChatClient();

        UsageRecordingChatClient.ResolveProviderName(inner, null).Should().Be("unknown");
        UsageRecordingChatClient.ResolveProviderName(inner, "  ").Should().Be("unknown");
    }

    [Fact]
    public void ResolveProviderName_RejectsANullClient()
    {
        var act = () => UsageRecordingChatClient.ResolveProviderName(null!, "Anthropic");

        act.Should().Throw<ArgumentNullException>();
    }
}
