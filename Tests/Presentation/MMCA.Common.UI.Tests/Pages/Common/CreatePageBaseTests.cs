using System.Globalization;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Common;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// Tests for <see cref="CreatePageBase"/> and the <see cref="CancellablePageBase"/> it derives from:
/// the submit pipeline every admin create form shares (validate, pre-check, save with the page
/// token, clear the dirty flag before the page's own success step, keep the failed Result for the
/// error summary, swallow a cancel, always end the saving state) and the page-token lifetime.
/// The probe page renders a real <see cref="MudForm"/> bound through <c>Form</c> with one required
/// text field, so validity comes from MudBlazor itself.
/// </summary>
public sealed class CreatePageBaseTests : BunitTestBase
{
    private const string ValidName = "Widget";

    private readonly Mock<IToastService> _toast = new();

    public CreatePageBaseTests()
    {
        // Last registration wins over the Mud-backed facade the base registered, so every toast the
        // submit pipeline raises (or must not raise) can be asserted exactly.
        Services.AddSingleton<IToastService>(_toast.Object);
    }

    private sealed record CreatedWidget(int Id);

    private static Task<Result<CreatedWidget>> Saved(int id = 7) =>
        Task.FromResult(Result.Success(new CreatedWidget(id)));

    private IRenderedComponent<ProbeCreatePage> RenderProbe(string? name = ValidName, bool withForm = true) =>
        Render<ProbeCreatePage>(p => p
            .Add(x => x.Name, name)
            .Add(x => x.WithForm, withForm));

    private static async Task<bool> SubmitOnDispatcherAsync<TCreated>(
        IRenderedComponent<ProbeCreatePage> cut,
        Func<CancellationToken, Task<Result<TCreated>>> saveAsync,
        Action<TCreated> onCreated,
        CreateSubmitOptions? options = null)
    {
        var submitted = false;
        await cut.InvokeAsync(async () =>
        {
            submitted = await cut.Instance.CallSubmitAsync(saveAsync, onCreated, options);
        });
        return submitted;
    }

    // == Dirty tracking (rule 14.4) ==
    [Fact]
    public void MarkDirty_SetsIsDirty()
    {
        var cut = RenderProbe();
        cut.Instance.Dirty.Should().BeFalse();

        cut.Instance.CallMarkDirty();

        cut.Instance.Dirty.Should().BeTrue();
    }

    // == Guards (rule 14.5) ==
    [Fact]
    public async Task SubmitAsync_NoForm_ReturnsFalse_SaveNotCalled()
    {
        var cut = RenderProbe(withForm: false);
        var saveCalls = 0;
        var preCheckCalls = 0;
        var created = 0;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ =>
            {
                saveCalls++;
                return Saved();
            },
            _ => created++,
            new CreateSubmitOptions
            {
                PreCheck = () =>
                {
                    preCheckCalls++;
                    return null;
                },
            });

        submitted.Should().BeFalse();
        saveCalls.Should().Be(0);
        preCheckCalls.Should().Be(0);
        created.Should().Be(0);
        cut.Instance.Saving.Should().BeFalse();
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitAsync_SecondCallWhileSaving_ReturnsFalse_SaveCalledOnce()
    {
        var cut = RenderProbe();
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;

        Func<CancellationToken, Task<Result<CreatedWidget>>> save = async _ =>
        {
            saveCalls++;
            saveStarted.TrySetResult();
            await releaseSave.Task;
            return Result.Success(new CreatedWidget(7));
        };

        Task<bool>? first = null;
        await cut.InvokeAsync(() =>
        {
            first = cut.Instance.CallSubmitAsync(save, _ => { });
        });
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await SubmitOnDispatcherAsync(cut, save, _ => { });

        second.Should().BeFalse("a submit while a save is in flight is ignored");
        saveCalls.Should().Be(1);

        releaseSave.SetResult();
        (await first!.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        saveCalls.Should().Be(1);
    }

    // == Validation and pre-check (rules 14.6-14.8) ==
    [Fact]
    public async Task SubmitAsync_InvalidForm_WarnsValidationError_SaveNotCalled_PreCheckNotCalled()
    {
        var cut = RenderProbe(name: null);
        var saveCalls = 0;
        var preCheckCalls = 0;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ =>
            {
                saveCalls++;
                return Saved();
            },
            _ => { },
            new CreateSubmitOptions
            {
                PreCheck = () =>
                {
                    preCheckCalls++;
                    return "Pick an event first.";
                },
            });

        submitted.Should().BeFalse();
        saveCalls.Should().Be(0);
        preCheckCalls.Should().Be(0, "pre-checks run only once the form is valid");
        cut.Instance.Saving.Should().BeFalse();
        _toast.Verify(t => t.Warning(ErrorMessages.ValidationError), Times.Once);
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitAsync_PreCheckMessage_WarnsThatMessage_SaveNotCalled()
    {
        var cut = RenderProbe();
        var saveCalls = 0;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ =>
            {
                saveCalls++;
                return Saved();
            },
            _ => { },
            new CreateSubmitOptions { PreCheck = () => "Pick an event first." });

        submitted.Should().BeFalse();
        saveCalls.Should().Be(0);
        cut.Instance.Saving.Should().BeFalse();
        _toast.Verify(t => t.Warning("Pick an event first."), Times.Once);
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitAsync_PreCheckNull_Saves()
    {
        var cut = RenderProbe();
        var saveCalls = 0;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ =>
            {
                saveCalls++;
                return Saved();
            },
            _ => { },
            new CreateSubmitOptions { PreCheck = () => null });

        submitted.Should().BeTrue();
        saveCalls.Should().Be(1);
    }

    [Fact]
    public async Task SubmitAsync_ClearsSaveResultBeforeValidating()
    {
        var cut = RenderProbe();

        // A failed save first, so there is a SaveResult to clear.
        await SubmitOnDispatcherAsync(
            cut,
            _ => Task.FromResult(Result.Failure<CreatedWidget>(Error.Failure("Widgets.SaveFailed", "Could not save."))),
            _ => { },
            new CreateSubmitOptions { ToastOnFailure = false });
        cut.Instance.CurrentSaveResult.Should().NotBeNull();
        var observedBefore = cut.Instance.SaveResultsSeenDuringValidation.Count;

        await SubmitOnDispatcherAsync(cut, _ => Saved(), _ => { });

        var observedDuringSecondSubmit = cut.Instance.SaveResultsSeenDuringValidation.Skip(observedBefore).ToList();
        observedDuringSecondSubmit.Should().NotBeEmpty("the second submit validates the form");
        observedDuringSecondSubmit.Should().AllSatisfy(
            seen => seen.Should().BeNull("SaveResult is cleared before the form is validated"));
    }

    // == Saving state and token (rule 14.9) ==
    [Fact]
    public async Task SubmitAsync_IsSavingTrueWhileSaveRuns_FalseAfter()
    {
        var cut = RenderProbe();
        bool? savingDuringSave = null;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ =>
            {
                savingDuringSave = cut.Instance.Saving;
                return Saved();
            },
            _ => { });

        submitted.Should().BeTrue();
        savingDuringSave.Should().BeTrue();
        cut.Instance.Saving.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitAsync_PassesPageToken()
    {
        var cut = RenderProbe();
        CancellationToken seen = default;

        await SubmitOnDispatcherAsync(
            cut,
            token =>
            {
                seen = token;
                return Saved();
            },
            _ => { });

        seen.Should().Be(cut.Instance.Token);
        seen.CanBeCanceled.Should().BeTrue();
    }

    // == Success (rules 14.10, 14.14) ==
    [Fact]
    public async Task SubmitAsync_Success_DirtyClearedBeforeOnCreated_SaveResultNull_ReturnsTrue()
    {
        var cut = RenderProbe();
        cut.Instance.CallMarkDirty();
        bool? dirtyInOnCreated = null;
        Result? saveResultInOnCreated = Result.Failure(Error.Failure("Sentinel", "not observed"));
        CreatedWidget? createdValue = null;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ => Saved(42),
            created =>
            {
                dirtyInOnCreated = cut.Instance.Dirty;
                saveResultInOnCreated = cut.Instance.CurrentSaveResult;
                createdValue = created;
            });

        submitted.Should().BeTrue();
        createdValue.Should().Be(new CreatedWidget(42));
        dirtyInOnCreated.Should().BeFalse("a navigation inside onCreated must not be blocked by the unsaved-changes guard");
        saveResultInOnCreated.Should().BeNull();
        cut.Instance.Dirty.Should().BeFalse();
        cut.Instance.CurrentSaveResult.Should().BeNull();
        cut.Instance.Saving.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitAsync_Success_RaisesNoToast()
    {
        var cut = RenderProbe();

        var submitted = await SubmitOnDispatcherAsync(cut, _ => Saved(), _ => { });

        submitted.Should().BeTrue();
        _toast.VerifyNoOtherCalls();
    }

    // == Failure (rule 14.11) ==
    [Fact]
    public async Task SubmitAsync_Failure_SetsSaveResult_KeepsDirty_ToastsWithOptionsLocalizer()
    {
        var cut = RenderProbe();
        cut.Instance.CallMarkDirty();
        var failed = Result.Failure<CreatedWidget>(Error.Failure("Widgets.SaveFailed", "Widgets.SaveFailedMessage"));
        var localizer = new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Widgets.SaveFailedMessage"] = "The widget could not be saved.",
        });
        var created = 0;

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ => Task.FromResult(failed),
            _ => created++,
            new CreateSubmitOptions { Localizer = localizer });

        submitted.Should().BeFalse();
        created.Should().Be(0);
        cut.Instance.CurrentSaveResult.Should().BeSameAs(failed);
        cut.Instance.Dirty.Should().BeTrue("a failed save leaves the form dirty");
        cut.Instance.Saving.Should().BeFalse();
        _toast.Verify(t => t.Show("The widget could not be saved.", ToastSeverity.Error), Times.Once);
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitAsync_Failure_ToastOnFailureFalse_NoToast()
    {
        var cut = RenderProbe();
        var failed = Result.Failure<CreatedWidget>(Error.Failure("Widgets.SaveFailed", "Could not save."));

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ => Task.FromResult(failed),
            _ => { },
            new CreateSubmitOptions { ToastOnFailure = false });

        submitted.Should().BeFalse();
        cut.Instance.CurrentSaveResult.Should().BeSameAs(failed);
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public void CreateSubmitOptions_ToastOnFailure_DefaultsToTrue() =>
        new CreateSubmitOptions().ToastOnFailure.Should().BeTrue();

    // == Exceptions and the saving flag (rules 14.12, 14.13) ==
    [Fact]
    public async Task SubmitAsync_Cancelled_ReturnsFalse_IsSavingFalse()
    {
        var cut = RenderProbe();
        var created = 0;

        var submitted = await SubmitOnDispatcherAsync<CreatedWidget>(
            cut,
            _ => throw new OperationCanceledException(),
            _ => created++);

        submitted.Should().BeFalse();
        created.Should().Be(0);
        cut.Instance.Saving.Should().BeFalse();
        _toast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitAsync_OnCreatedCancelled_ReturnsFalse_IsSavingFalse()
    {
        var cut = RenderProbe();

        var submitted = await SubmitOnDispatcherAsync(
            cut,
            _ => Saved(),
            _ => throw new OperationCanceledException());

        submitted.Should().BeFalse();
        cut.Instance.Saving.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitAsync_SaveThrows_Propagates_IsSavingFalse()
    {
        var cut = RenderProbe();
        var boom = new InvalidOperationException("backend down");

        var act = () => SubmitOnDispatcherAsync<CreatedWidget>(cut, _ => throw boom, _ => { });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        cut.Instance.Saving.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitAsync_OnCreatedThrows_Propagates_IsSavingFalse()
    {
        var cut = RenderProbe();
        var boom = new InvalidOperationException("navigation failed");

        var act = () => SubmitOnDispatcherAsync(cut, _ => Saved(), _ => throw boom);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        cut.Instance.Saving.Should().BeFalse();
    }

    // == CancellablePageBase (rules 14.1, 14.2) ==
    [Fact]
    public void Dispose_CancelsPageToken_SecondDisposeIsNoOp()
    {
        var page = new CancellableProbe();
        var token = page.Token;
        token.IsCancellationRequested.Should().BeFalse();

        page.Dispose();

        token.IsCancellationRequested.Should().BeTrue();
        page.TokenCancelledInsideOverrideBeforeBase.Should().BeFalse(
            "the override releases its own resources first; the base cancels when the override calls it");

        var again = page.Dispose;
        again.Should().NotThrow();
        page.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void PageToken_ReadOnlyAfterDispose_IsCancelledAndDoesNotThrow()
    {
        var page = new CancellableProbe();

        page.Dispose();

        var read = () => page.Token;
        read.Should().NotThrow().Which.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Dispose_CallsDisposeBoolWithTrue()
    {
        var page = new CancellableProbe();

        page.Dispose();

        page.DisposingFlags.Should().Equal([true], "Dispose() runs Dispose(true) once");
    }

    [Fact]
    public async Task CreatePage_RenderedPage_TokenIsCancelledWithTheRenderer()
    {
        var cut = RenderProbe();
        var token = cut.Instance.Token;

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue();
    }

    /// <summary>
    /// The probe create page: a <see cref="MudForm"/> bound through <c>Form</c> wrapping one required
    /// text field whose custom validator records the <c>SaveResult</c> it sees, so the tests can tell
    /// whether the result was cleared before validation ran.
    /// </summary>
    private sealed class ProbeCreatePage : CreatePageBase
    {
        [Parameter] public string? Name { get; set; }

        [Parameter] public bool WithForm { get; set; } = true;

        public List<Result?> SaveResultsSeenDuringValidation { get; } = [];

        public bool Saving => IsSaving;

        public bool Dirty => IsDirty;

        public Result? CurrentSaveResult => SaveResult;

        public CancellationToken Token => PageToken;

        public void CallMarkDirty() => MarkDirty();

        public Task<bool> CallSubmitAsync<TCreated>(
            Func<CancellationToken, Task<Result<TCreated>>> saveAsync,
            Action<TCreated> onCreated,
            CreateSubmitOptions? options = null) =>
            SubmitAsync(saveAsync, onCreated, options);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!WithForm)
            {
                builder.AddContent(0, "no form");
                return;
            }

            builder.OpenComponent<MudForm>(1);
            builder.AddAttribute(2, nameof(MudForm.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<MudTextField<string>>(3);
                child.AddAttribute(4, nameof(MudTextField<string>.Value), Name);
                child.AddAttribute(5, nameof(MudTextField<string>.Required), true);
                child.AddAttribute(6, nameof(MudTextField<string>.RequiredError), "Name is required.");
                child.AddAttribute(7, nameof(MudTextField<string>.Validation), new Func<string, string?>(_ =>
                {
                    SaveResultsSeenDuringValidation.Add(SaveResult);
                    return null;
                }));
                child.CloseComponent();
            }));
            builder.AddComponentReferenceCapture(8, form => Form = (MudForm)form);
            builder.CloseComponent();
        }
    }

    /// <summary>A direct <see cref="CancellablePageBase"/> subclass whose override records the dispose order.</summary>
    private sealed class CancellableProbe : CancellablePageBase
    {
        public CancellationToken Token => PageToken;

        public List<bool> DisposingFlags { get; } = [];

        public bool? TokenCancelledInsideOverrideBeforeBase { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposingFlags.Add(disposing);
            TokenCancelledInsideOverrideBeforeBase ??= PageToken.IsCancellationRequested;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A minimal page localizer: it answers only the keys it was given and reports every other key as
    /// <c>ResourceNotFound</c>, so the failure toast proves which localizer resolved the message.
    /// </summary>
    private sealed class StubLocalizer(Dictionary<string, string> values) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            values.TryGetValue(name, out var value)
                ? new LocalizedString(name, string.Format(CultureInfo.CurrentCulture, value, arguments), resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            values.Select(pair => new LocalizedString(pair.Key, pair.Value));
    }
}
