using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Users.UseCases.ExportUserData;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Privacy;
using Moq;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared data-subject export workflow: the owner-or-privileged-role gate, the
/// read-only account load, the app's subject snapshot, and the best-effort section fan-out that must
/// degrade rather than fail.
/// </summary>
public sealed class ExportUserDataHandlerBaseTests
{
    private const string PrivilegedRole = "Organizer";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 13, 17, 42, 9, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenCallerIsNeitherOwnerNorPrivileged_ReturnsForbiddenWithoutFetchingOrExporting()
    {
        var section = new RecordingSection("Engagement");
        var (sut, mocks) = CreateSut(section);

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 2, "Attendee"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == "User.ExportForbidden"
            && e.Type == ErrorType.Forbidden
            && e.Source == nameof(TestExportUserDataHandler)
            && e.Target == "UserId");
        mocks.Repository.Verify(
            x => x.GetByIdAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        section.Invocations.Should().Be(0, "a forbidden caller must never reach the data");
    }

    [Fact]
    public async Task HandleAsync_WhenCallerHoldsPrivilegedRole_ExportsAnotherAccount()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 2, PrivilegedRole));

        result.IsSuccess.Should().BeTrue();
        result.Value!.UserId.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WhenUserMissing_ReturnsNotFound()
    {
        var (sut, mocks) = CreateSut();
        mocks.Repository
            .Setup(x => x.GetByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestIdentityUser?)null);

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 404, CurrentUserId: 404, null));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Type == ErrorType.NotFound && e.Target == nameof(TestIdentityUser));
    }

    [Fact]
    public async Task HandleAsync_ReadsTheAccountThroughTheReadRepository()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        await sut.HandleAsync(new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        mocks.UnitOfWork.Verify(
            x => x.GetReadRepository<TestIdentityUser, UserIdentifierType>(),
            Times.Once,
            "a query handler never saves, so it takes the no-tracking read repository");
        mocks.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_AssemblesTheEnvelopeFromTheSnapshotAndSections()
    {
        var payload = new { Bookmarks = 2 };
        var (sut, mocks) = CreateSut(new RecordingSection("Engagement", UserDataExportSectionResult.Complete("Engagement", payload)));
        ArrangeUser(mocks, new TestIdentityUser { Id = 7 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 7, CurrentUserId: 7, null));

        result.IsSuccess.Should().BeTrue();
        UserDataExportDTO export = result.Value!;
        export.FormatVersion.Should().Be("1.0");
        export.UserId.Should().Be(7);
        export.Subject.Should().BeSameAs(sut.LastSnapshot);
        export.Sections.Should().ContainSingle();
        export.Sections[0].SectionName.Should().Be("Engagement");
        export.Sections[0].Available.Should().BeTrue();
        export.Sections[0].Data.Should().BeSameAs(payload);
        export.Sections[0].UnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_StampsGeneratedOnFromTheTimeProvider()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        result.Value!.GeneratedOn.Should().Be(FixedNow,
            "the stamp comes from the injected time provider, not from DateTime.UtcNow");
    }

    [Fact]
    public async Task HandleAsync_WithNoSections_StillProducesTheEnvelope()
    {
        var (sut, mocks) = CreateSut();
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Sections.Should().BeEmpty("an app with no contributors still owes the subject its account data");
        result.Value.Subject.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenASectionThrows_DegradesThatSectionAndKeepsTheRest()
    {
        var throwing = new ThrowingSection("Engagement");
        var healthy = new RecordingSection("Notifications", UserDataExportSectionResult.Complete("Notifications", data: null));
        var (sut, mocks) = CreateSut(throwing, healthy);
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        result.IsSuccess.Should().BeTrue("one failing contributor must never deny the subject the whole export");
        result.Value!.Sections.Should().HaveCount(2);
        result.Value.Sections[0].SectionName.Should().Be("Engagement");
        result.Value.Sections[0].Available.Should().BeFalse();
        result.Value.Sections[0].Data.Should().BeNull();
        result.Value.Sections[0].UnavailableReason
            .Should().Be(UserDataExportSectionDefaults.UnavailableReason)
            .And.NotContain("Engagement peer exploded", "internal exception detail never reaches the data subject");
        result.Value.Sections[1].Available.Should().BeTrue();
        healthy.Invocations.Should().Be(1, "a failing section does not short-circuit the fan-out");
    }

    [Fact]
    public async Task HandleAsync_WhenASectionReportsItselfUnavailable_CarriesItsOwnReason()
    {
        var section = new RecordingSection(
            "Sales",
            UserDataExportSectionResult.Unavailable("Sales", "The orders service is temporarily unreachable."));
        var (sut, mocks) = CreateSut(section);
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        result.Value!.Sections[0].Available.Should().BeFalse();
        result.Value.Sections[0].UnavailableReason.Should().Be("The orders service is temporarily unreachable.");
    }

    [Fact]
    public async Task HandleAsync_PreservesRegistrationOrderOfSections()
    {
        var (sut, mocks) = CreateSut(
            new RecordingSection("Alpha"),
            new RecordingSection("Bravo"),
            new RecordingSection("Charlie"));
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        result.Value!.Sections.Select(s => s.SectionName).Should().Equal("Alpha", "Bravo", "Charlie");
    }

    [Fact]
    public async Task HandleAsync_PassesTheSubjectIdToEverySection()
    {
        var section = new RecordingSection("Engagement");
        var (sut, mocks) = CreateSut(section);
        ArrangeUser(mocks, new TestIdentityUser { Id = 7 });

        await sut.HandleAsync(new TestExportUserDataQuery(UserId: 7, CurrentUserId: 7, PrivilegedRole));

        section.LastUserId.Should().Be(7, "the export is about the target account, not the caller");
    }

    [Fact]
    public async Task HandleAsync_RunsTheCompletionHookWithTheAssembledPackage()
    {
        var (sut, mocks) = CreateSut(new RecordingSection("Engagement"));
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Result<UserDataExportDTO> result = await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        sut.CompletedExport.Should().BeSameAs(result.Value);
        sut.CompletedExport!.Sections.Should().ContainSingle("the hook sees the finished document, not a partial one");
    }

    [Fact]
    public async Task HandleAsync_WhenASectionIsCancelled_PropagatesRatherThanDegrading()
    {
        var (sut, mocks) = CreateSut(new CancellingSection("Engagement"));
        ArrangeUser(mocks, new TestIdentityUser { Id = 1 });

        Func<Task> act = async () => await sut.HandleAsync(
            new TestExportUserDataQuery(UserId: 1, CurrentUserId: 1, null));

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation is the caller giving up, not a contributor failing");
    }

    private static void ArrangeUser(HandlerMocks mocks, TestIdentityUser user) =>
        mocks.Repository
            .Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

    private sealed record HandlerMocks(
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IReadRepository<TestIdentityUser, UserIdentifierType>> Repository);

    private static (TestExportUserDataHandler Sut, HandlerMocks Mocks) CreateSut(
        params IUserDataExportSection[] sections)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var repository = new Mock<IReadRepository<TestIdentityUser, UserIdentifierType>>();

        unitOfWork.Setup(x => x.GetReadRepository<TestIdentityUser, UserIdentifierType>()).Returns(repository.Object);

        var sut = new TestExportUserDataHandler(unitOfWork.Object, sections, new FakeTimeProvider(FixedNow));
        return (sut, new HandlerMocks(unitOfWork, repository));
    }
}

/// <summary>Concrete subclass standing in for an app's <c>ExportUserDataHandler</c>.</summary>
public sealed class TestExportUserDataHandler(
    IUnitOfWork unitOfWork,
    IEnumerable<IUserDataExportSection> sections,
    TimeProvider timeProvider)
    : ExportUserDataHandlerBase<TestIdentityUser, TestExportUserDataQuery>(
        unitOfWork, sections, timeProvider, NullLogger.Instance)
{
    public object? LastSnapshot { get; private set; }

    public UserDataExportDTO? CompletedExport { get; private set; }

    protected override bool HasExportPrivilege(string? currentUserRole) =>
        string.Equals(currentUserRole, "Organizer", StringComparison.OrdinalIgnoreCase);

    protected override Task<object?> BuildSubjectSnapshotAsync(
        TestIdentityUser user,
        TestExportUserDataQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        LastSnapshot = new { user.Id, user.PreferredCulture };
        return Task.FromResult<object?>(LastSnapshot);
    }

    protected override Task OnExportCompletedAsync(
        TestIdentityUser user,
        TestExportUserDataQuery query,
        UserDataExportDTO export,
        CancellationToken cancellationToken)
    {
        CompletedExport = export;
        return Task.CompletedTask;
    }
}

/// <summary>A section that records its calls and hands back a canned result.</summary>
public sealed class RecordingSection(string sectionName, UserDataExportSectionResult? result = null)
    : IUserDataExportSection
{
    public string SectionName { get; } = sectionName;

    public int Invocations { get; private set; }

    public UserIdentifierType LastUserId { get; private set; }

    public Task<UserDataExportSectionResult> ExportAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        Invocations++;
        LastUserId = userId;
        return Task.FromResult(result ?? UserDataExportSectionResult.Complete(SectionName, data: null));
    }
}

/// <summary>A section standing in for an unreachable peer.</summary>
public sealed class ThrowingSection(string sectionName) : IUserDataExportSection
{
    public string SectionName { get; } = sectionName;

    public Task<UserDataExportSectionResult> ExportAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Engagement peer exploded");
}

/// <summary>A section standing in for a cancelled request.</summary>
public sealed class CancellingSection(string sectionName) : IUserDataExportSection
{
    public string SectionName { get; } = sectionName;

    public Task<UserDataExportSectionResult> ExportAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default) =>
        throw new OperationCanceledException();
}
