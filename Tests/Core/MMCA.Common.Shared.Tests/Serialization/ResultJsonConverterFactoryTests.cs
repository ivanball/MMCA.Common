using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Serialization;

/// <summary>
/// Round-trip tests for the Result JSON converter. The distributed query cache serializes
/// cached handler results (typically <c>Result&lt;PagedCollectionResult&lt;TDTO&gt;&gt;</c>) to Redis,
/// so every shape the caching decorator can produce must survive serialize + deserialize.
/// </summary>
public class ResultJsonConverterFactoryTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private sealed record TestDTO(int Id, string Name);

    // ── Non-generic Result ──
    [Fact]
    public void NonGenericSuccess_RoundTrips()
    {
        var json = JsonSerializer.Serialize(Result.Success());
        var roundTripped = JsonSerializer.Deserialize<Result>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsSuccess.Should().BeTrue();
        roundTripped.Errors.Should().BeEmpty();
    }

    [Fact]
    public void NonGenericFailure_RoundTripsErrors()
    {
        var original = Result.Failure([Error.Validation("Test.Code", "Broken", source: "UnitTest", target: "Field")]);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<Result>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsFailure.Should().BeTrue();
        roundTripped.Errors.Should().ContainSingle()
            .Which.Should().Be(original.Errors[0]);
    }

    // ── Generic Result<T> ──
    [Fact]
    public void GenericSuccess_RoundTripsValue()
    {
        var original = Result.Success(new TestDTO(42, "Answer"));

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<Result<TestDTO>>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsSuccess.Should().BeTrue();
        roundTripped.Value.Should().Be(original.Value);
    }

    [Fact]
    public void GenericFailure_RoundTripsErrorsWithNullValue()
    {
        var original = Result.Failure<TestDTO>(Error.NotFound);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<Result<TestDTO>>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsFailure.Should().BeTrue();
        roundTripped.Value.Should().BeNull();
        roundTripped.Errors.Should().ContainSingle()
            .Which.Type.Should().Be(ErrorType.NotFound);
    }

    // ── The distributed-cache shape: Result<PagedCollectionResult<T>> under Web options ──
    [Fact]
    public void PagedCollectionResult_UnderWebOptions_RoundTrips()
    {
        var payload = new PagedCollectionResult<TestDTO>(
            items: [new TestDTO(1, "One"), new TestDTO(2, "Two")],
            paginationMetadata: new PaginationMetadata(totalItemCount: 2, pageSize: 10, currentPage: 1));
        var original = Result.Success(payload);

        var json = JsonSerializer.Serialize(original, WebOptions);
        var roundTripped = JsonSerializer.Deserialize<Result<PagedCollectionResult<TestDTO>>>(json, WebOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsSuccess.Should().BeTrue();
        roundTripped.Value.Should().NotBeNull();
        roundTripped.Value!.Items.Should().BeEquivalentTo(payload.Items);
        roundTripped.Value.PaginationMetadata.TotalItemCount.Should().Be(2);
        roundTripped.Value.PaginationMetadata.PageSize.Should().Be(10);
        roundTripped.Value.PaginationMetadata.CurrentPage.Should().Be(1);
    }

    [Fact]
    public void SuccessWithNullValue_RoundTripsAsSuccess()
    {
        var original = Result.Success<TestDTO?>(null);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<Result<TestDTO?>>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsSuccess.Should().BeTrue();
        roundTripped.Value.Should().BeNull();
    }

    [Fact]
    public void UnknownProperties_AreSkipped()
    {
        const string json = """{"value":{"id":7,"name":"X"},"stale":true,"extra":{"nested":[1,2]}}""";

        var roundTripped = JsonSerializer.Deserialize<Result<TestDTO>>(json, WebOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.IsSuccess.Should().BeTrue();
        roundTripped.Value.Should().Be(new TestDTO(7, "X"));
    }
}
