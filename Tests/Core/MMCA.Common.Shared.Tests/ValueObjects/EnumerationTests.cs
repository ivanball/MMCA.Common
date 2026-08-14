using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

/// <summary>
/// Pins the smart-enumeration contract: reflection-based member discovery, Result-returning lookups,
/// and type-guarded equality (two enumerations that happen to share an integer value are never equal).
/// </summary>
public class EnumerationTests
{
    // ── FromValue ──
    [Fact]
    public void FromValue_WithDeclaredValue_ReturnsTheDeclaredInstance()
    {
        var result = Priority.FromValue(2);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(Priority.Normal, "lookups hand back the interned singleton, not a copy");
    }

    [Fact]
    public void FromValue_WithUnknownValue_ReturnsUnknownValueError()
    {
        var result = Priority.FromValue(99);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Enumeration.UnknownValue");
    }

    // ── FromName ──
    [Fact]
    public void FromName_WithDeclaredName_ReturnsTheDeclaredInstance()
    {
        var result = Priority.FromName("High");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(Priority.High);
    }

    [Fact]
    public void FromName_WithDifferentCasing_ReturnsTheDeclaredInstance()
    {
        var result = Priority.FromName("hIGh");

        result.IsSuccess.Should().BeTrue("name lookup is case-insensitive, matching RoleValue");
        result.Value.Should().BeSameAs(Priority.High);
    }

    [Fact]
    public void FromName_WithUnknownName_ReturnsUnknownNameError()
    {
        var result = Priority.FromName("Urgent");

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Enumeration.UnknownName");
    }

    [Fact]
    public void FromName_WithNull_ReturnsUnknownNameError()
    {
        var result = Priority.FromName(null!);

        result.IsFailure.Should().BeTrue("a null name must fail as a lookup miss, not throw");
        result.Errors.Should().Contain(e => e.Code == "Enumeration.UnknownName");
    }

    // ── All ──
    [Fact]
    public void All_ContainsEveryDeclaredMemberOrderedByValue()
    {
        Priority.All.Should().HaveCount(3);
        Priority.All.Should().ContainInOrder(Priority.Low, Priority.Normal, Priority.High);
    }

    [Fact]
    public void All_IsScopedToTheDeclaringEnumeration()
    {
        Severity.All.Should().HaveCount(2, "each closed type gets its own lookup tables");
        Severity.All.Should().ContainInOrder(Severity.Trivial, Severity.Blocking);
    }

    // ── Equality ──
    [Fact]
    public void Equals_SameMember_IsTrue()
        => Priority.High.Should().Be(Priority.High);

    [Fact]
    public void Equals_DifferentMembersOfTheSameEnumeration_IsFalse()
        => Priority.High.Should().NotBe(Priority.Low);

    [Fact]
    public void Equals_MembersOfDifferentEnumerationsSharingAValue_IsFalse()
    {
        Priority.Low.Value.Should().Be(Severity.Trivial.Value, "the two members share an integer value");

        Priority.Low.Equals(Severity.Trivial).Should().BeFalse(
            "equality is type-guarded, so an integer value alone never makes two enumerations equal");
    }

    [Fact]
    public void Equals_NonEnumerationObject_IsFalse()
        => Priority.Low.Equals("Low").Should().BeFalse();

    [Fact]
    public void GetHashCode_SameMember_IsStable()
        => Priority.High.GetHashCode().Should().Be(Priority.High.GetHashCode());

    [Fact]
    public void GetHashCode_MembersOfDifferentEnumerationsSharingAValue_StayDistinctEntries()
    {
        var set = new HashSet<object>(2) { Priority.Low, Severity.Trivial };

        set.Should().HaveCount(2, "the concrete type participates in both equality and hashing");
    }

    // ── ToString ──
    [Fact]
    public void ToString_ReturnsTheMemberName()
        => Priority.Normal.ToString().Should().Be("Normal");

    private sealed class Priority : Enumeration<Priority>
    {
        public static readonly Priority Low = new(1, "Low");
        public static readonly Priority Normal = new(2, "Normal");
        public static readonly Priority High = new(3, "High");

        private Priority(int value, string name)
            : base(value, name)
        {
        }
    }

    private sealed class Severity : Enumeration<Severity>
    {
        public static readonly Severity Trivial = new(1, "Trivial");
        public static readonly Severity Blocking = new(2, "Blocking");

        private Severity(int value, string name)
            : base(value, name)
        {
        }
    }
}
