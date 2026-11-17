using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Users.UseCases.DeleteUser;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Users.UseCases.DeleteUser;

/// <summary>
/// Exercises the erasure's durable push-installation cleanup: it deletes through the registrar's
/// owner-scoped delete-all and hands a failure back unchanged, so the processor retries the row.
/// </summary>
public sealed class DeleteUserPushInstallationsInternalCommandHandlerTests
{
    private readonly Mock<IPushDeviceRegistrar> _registrar = new();

    [Fact]
    public async Task HandleAsync_DeletesEveryInstallationOfTheErasedUser()
    {
        _registrar.Setup(x => x.DeleteAllForUserAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());

        var result = await CreateSut().HandleAsync(new DeleteUserPushInstallationsInternalCommand(42), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _registrar.Verify(x => x.DeleteAllForUserAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenTheProviderFails_ReturnsTheFailureSoTheRowRetries()
    {
        var failure = Result.Failure(Error.Failure("PushDevice.DeleteFailed", "down"));
        _registrar.Setup(x => x.DeleteAllForUserAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(failure);

        var result = await CreateSut().HandleAsync(new DeleteUserPushInstallationsInternalCommand(42), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "PushDevice.DeleteFailed");
    }

    private DeleteUserPushInstallationsInternalCommandHandler CreateSut() =>
        new(_registrar.Object, NullLogger<DeleteUserPushInstallationsInternalCommandHandler>.Instance);
}
