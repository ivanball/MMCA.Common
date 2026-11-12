using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <c>pipeline.AddWebhookDelivery()</c> (rule 35.6): the delivery handler is registered inside the
/// pipeline so every decorator wraps it, and a host that forgot <c>AddWebhooks(configuration)</c>
/// fails at startup with a message naming it. Also pins the internal-command contract of
/// <see cref="DeliverWebhookInternalCommand"/> (rule 35.36: the row is the delivery history).
/// </summary>
public sealed class AddWebhookDeliveryTests
{
    [Fact]
    public void Pipeline_LeavesDeliveryHandlerDecorated()
    {
        var services = new ServiceCollection();

        services.AddMmcaApplicationPipeline(pipeline => pipeline.AddWebhookDelivery());

        Action verify = () => services.VerifyDecoratorPipeline();
        verify.Should().NotThrow();

        // Decorated: Scrutor rewrote the implementation-type registration into a factory.
        var descriptor = services.Last(d =>
            !d.IsKeyedService && d.ServiceType == typeof(ICommandHandler<DeliverWebhookInternalCommand, Result>));
        descriptor.ImplementationType.Should().BeNull();
        descriptor.ImplementationFactory.Should().NotBeNull();
    }

    [Fact]
    public void AddWebhookDelivery_ReturnsTheBuilder_ForChaining()
    {
        var services = new ServiceCollection();
        MmcaApplicationPipelineBuilder? returned = null;
        MmcaApplicationPipelineBuilder? given = null;

        services.AddMmcaApplicationPipeline(pipeline =>
        {
            given = pipeline;
            returned = pipeline.AddWebhookDelivery();
        });

        returned.Should().BeSameAs(given);
    }

    [Fact]
    public void WithoutInfrastructure_StartupValidationNamesAddWebhooks()
    {
        var services = new ServiceCollection();
        services.AddMmcaApplicationPipeline(pipeline => pipeline.AddWebhookDelivery());
        using var provider = services.BuildServiceProvider();

        Action resolve = () => _ = provider.GetRequiredService<IOptions<WebhookSettings>>().Value;

        resolve.Should().Throw<OptionsValidationException>()
            .WithMessage("*AddWebhooks(configuration)*");
    }

    [Fact]
    public void WithAStoreRegistered_StartupValidationPasses()
    {
        var services = new ServiceCollection();
        services.AddMmcaApplicationPipeline(pipeline => pipeline.AddWebhookDelivery());
        services.AddScoped(_ => Mock.Of<IWebhookSubscriptionStore>());
        using var provider = services.BuildServiceProvider();

        Action resolve = () => _ = provider.GetRequiredService<IOptions<WebhookSettings>>().Value;

        resolve.Should().NotThrow();
    }

    [Fact]
    public void Settings_HaveTheDocumentedDefaults()
    {
        var settings = new WebhookSettings();

        WebhookSettings.SectionName.Should().Be("Webhooks");
        settings.Enabled.Should().BeFalse();
        settings.DataSourceName.Should().Be("Default");
        settings.TimeoutSeconds.Should().Be(10);
        settings.MaxPayloadBytes.Should().Be(65536);
        settings.PreviousSecretGraceHours.Should().Be(24);
        settings.MaxSubscriptionsPerTenant.Should().Be(25);
        settings.AllowInsecureTargetsInDevelopment.Should().BeFalse();
    }

    [Fact]
    public void DeliverCommand_HasItsStableInternalCommandName()
    {
        typeof(DeliverWebhookInternalCommand).GetCustomAttribute<InternalCommandNameAttribute>()!.Name
            .Should().Be("MMCA.Webhooks.Deliver.v1", "a stored row is resolved by this name, so it never changes");
        typeof(IInternalCommand).IsAssignableFrom(typeof(DeliverWebhookInternalCommand)).Should().BeTrue();
    }

    [Fact]
    public void DeliverCommand_RoundTripsThroughInternalCommandJsonOptions()
    {
        // Mirrors InternalCommandMessage's private serializer options (Infrastructure,
        // InternalCommandMessage.cs:23-26), which serialize by the command's runtime type.
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles };
        IInternalCommand command = new DeliverWebhookInternalCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ticket.opened",
            Guid.NewGuid(),
            new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            """{"ticketId":7,"title":"Quote \" and a backslash \\ inside"}""");

        var payload = JsonSerializer.Serialize(command, command.GetType(), options);
        var restored = JsonSerializer.Deserialize<DeliverWebhookInternalCommand>(payload, options);

        restored.Should().Be(command);
        restored!.OccurredOn.Kind.Should().Be(DateTimeKind.Utc);
    }
}
