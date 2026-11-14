using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Components.Auth;

/// <summary>
/// bUnit tests for <see cref="PasskeysPanel"/>, the Profile page's passkeys section (ADR-139): it lists
/// the caller's passkeys by name, adds one through the WebAuthn ceremony (offered only where the head
/// can run it), renames one, and deletes one after a confirmation, showing the server's reason when the
/// delete is refused.
/// </summary>
public sealed class PasskeysPanelTests : BunitTestBase
{
    private static readonly DateTime CreatedOn = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IPasskeyUIService> _passkeys = new();
    private readonly Mock<IPasskeyCapability> _capability = new();
    private readonly Mock<IAppDialogService> _dialogs = new();
    private readonly Mock<IToastService> _toasts = new();
    private readonly List<PasskeyResponse> _rows = [new(Guid.NewGuid(), "Phone", CreatedOn, null)];

    public PasskeysPanelTests()
    {
        _passkeys.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result.Success<IReadOnlyList<PasskeyResponse>>([.. _rows]));
        _capability.SetupGet(x => x.IsAvailable).Returns(true);
        Services.AddSingleton(_passkeys.Object);
        Services.AddSingleton(_capability.Object);
        Services.AddSingleton(_dialogs.Object);
        Services.AddSingleton(_toasts.Object);
    }

    [Fact]
    public void ListsEachPasskeyByName()
    {
        _rows.Add(new PasskeyResponse(Guid.NewGuid(), "Work laptop", CreatedOn, CreatedOn.AddDays(2)));

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-row']").Should().HaveCount(2));
        cut.Markup.Should().Contain("Phone").And.Contain("Work laptop");
    }

    // -- Run 17 U-60: the dates are the viewer's local date, not the UTC date --
    [Fact]
    public void AddedAndLastUsed_ShowTheViewersLocalDate()
    {
        // 03:20 UTC on 11/14 is 22:20 on 11/13 in New York (EST, UTC-5).
        var lateEvening = new DateTime(2026, 11, 14, 3, 20, 0, DateTimeKind.Utc);
        _rows.Clear();
        _rows.Add(new PasskeyResponse(Guid.NewGuid(), "Phone", lateEvening, lateEvening));
        JSInterop.SetupModule("./_content/MMCA.Common.UI/time-zone.js")
            .Setup<string?>("getTimeZone").SetResult("America/New_York");
        var localDate = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Unspecified).ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        var utcDate = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Unspecified).ToString("d", System.Globalization.CultureInfo.CurrentCulture);

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });

        cut.WaitForAssertion(() =>
        {
            var cells = cut.Find("[data-testid='passkeys-row']").ParentElement!.QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToList();
            cells.Count(text => text == localDate).Should().Be(2, "both the added and the last-used cell show the local date, cells: " + string.Join(" | ", cells));
            cells.Should().NotContain(utcDate);
        });
    }

    [Fact]
    public void TheAddButton_IsHidden_WhenTheCapabilityIsUnavailable()
    {
        Services.AddSingleton<IPasskeyCapability, UnavailablePasskeyCapability>();

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-row']").Should().ContainSingle());
        cut.FindAll("[data-testid='passkeys-add']").Should().BeEmpty();
    }

    [Fact]
    public void Add_RunsTheCeremony_SendsTheAttestation_AndShowsTheNewPasskey()
    {
        var options = JsonSerializer.SerializeToElement(new { challenge = "abc" });
        var attestation = JsonSerializer.SerializeToElement(new { id = "new-cred" });
        _passkeys.Setup(x => x.BeginRegistrationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PasskeyOptionsResponse("flow-9", options)));
        _capability.Setup(x => x.CreateAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>())).ReturnsAsync(attestation);
        _passkeys.Setup(x => x.CompleteRegistrationAsync(It.IsAny<PasskeyRegisterRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var added = new PasskeyResponse(Guid.NewGuid(), "Security key", CreatedOn, null);
                _rows.Add(added);
                return Result.Success(added);
            });

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-row']").Should().ContainSingle());
        cut.Find("input[data-testid='passkeys-new-name']").Change("Security key");
        cut.Find("[data-testid='passkeys-add']").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-row']").Should().HaveCount(2));
        _passkeys.Verify(
            x => x.CompleteRegistrationAsync(
                It.Is<PasskeyRegisterRequest>(r => r.FlowId == "flow-9" && r.Name == "Security key" && r.Credential.GetProperty("id").GetString() == "new-cred"),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Theory]
    [InlineData("en", "sign out and sign in again")]
    [InlineData("es", "cierra la sesión y vuelve a iniciarla")]
    public void Add_RefusedForAStaleSignIn_SaysToSignInAgain(string culture, string expected)
    {
        _passkeys.Setup(x => x.BeginRegistrationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<PasskeyOptionsResponse>(
                Error.Forbidden("Passkeys.StepUpRequired", "Sign in again before adding a passkey.")));
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var cut = RenderUnderTest<PasskeysPanel>(_ => { });
            cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-add']").Should().ContainSingle());
            cut.Find("[data-testid='passkeys-add']").Click();

            cut.WaitForAssertion(() => _toasts.Verify(
                x => x.Error(It.Is<string>(m => m.Contains(expected, StringComparison.OrdinalIgnoreCase))),
                Times.Once()));
            _capability.Verify(x => x.CreateAsync(It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Never());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Rename_SendsTheNewName()
    {
        var id = _rows[0].Id;
        _passkeys.Setup(x => x.RenameAsync(id, "Old phone", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new PasskeyResponse(id, "Old phone", CreatedOn, null)));

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-rename']").Should().ContainSingle());
        cut.Find("[data-testid='passkeys-rename']").Click();
        cut.Find("input[data-testid='passkeys-rename-input']").Change("Old phone");
        cut.Find("[data-testid='passkeys-rename-save']").Click();

        cut.WaitForAssertion(() => _passkeys.Verify(x => x.RenameAsync(id, "Old phone", It.IsAny<CancellationToken>()), Times.Once()));
    }

    [Fact]
    public void Delete_AfterTheConfirmation_DeletesThePasskey()
    {
        var id = _rows[0].Id;
        _dialogs.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        _passkeys.Setup(x => x.DeleteAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _rows.Clear();
                return Result.Success();
            });

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-delete']").Should().ContainSingle());
        cut.Find("[data-testid='passkeys-delete']").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-row']").Should().BeEmpty());
        _passkeys.Verify(x => x.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void Delete_WhenTheUserDeclinesTheConfirmation_DeletesNothing()
    {
        _dialogs.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-delete']").Should().ContainSingle());
        cut.Find("[data-testid='passkeys-delete']").Click();

        _passkeys.Verify(x => x.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void Delete_RefusedAsTheLastPasskey_ShowsTheReason()
    {
        var id = _rows[0].Id;
        _dialogs.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        _passkeys.Setup(x => x.DeleteAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Conflict("Passkeys.LastPasskey", "This passkey is the only way to sign in to this account, so it cannot be deleted.")));

        var cut = RenderUnderTest<PasskeysPanel>(_ => { });
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='passkeys-delete']").Should().ContainSingle());
        cut.Find("[data-testid='passkeys-delete']").Click();

        cut.WaitForAssertion(() => _toasts.Verify(x => x.Error(It.Is<string>(m => m.Contains("only way to sign in", StringComparison.Ordinal))), Times.Once()));
        cut.FindAll("[data-testid='passkeys-row']").Should().ContainSingle();
    }
}
