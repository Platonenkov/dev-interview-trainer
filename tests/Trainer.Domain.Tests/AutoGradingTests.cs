using Trainer.Domain.Grading;

namespace Trainer.Domain.Tests;

public sealed class AutoGradingTests
{
    [Theory]
    [InlineData(new[] { "a", "b", "c" }, new[] { "a", "b", "c" }, false, Grade.Knew)]
    [InlineData(new[] { "a", "b", "c" }, new[] { "c", "a", "b" }, true, Grade.Partial)]
    [InlineData(new[] { "a", "b", "c" }, new[] { "a", "b" }, false, Grade.Partial)]
    [InlineData(new[] { "a", "b" }, new[] { "a", "b", "d" }, false, Grade.Partial)]
    [InlineData(new[] { "a", "b", "c" }, new[] { "a" }, false, Grade.DidntKnow)]
    [InlineData(new[] { "a", "b" }, new[] { "a", "c", "d" }, false, Grade.DidntKnow)]
    [InlineData(new[] { "a" }, new[] { "b" }, false, Grade.DidntKnow)]
    [InlineData(new[] { "a", "b" }, new string[0], false, Grade.DidntKnow)]
    public void Multi_FollowsSpecRule(string[] correct, string[] picked, bool hint, Grade expected) =>
        Assert.Equal(expected, AutoGrading.Multi(correct, picked, hint));

    [Fact]
    public void Multi_EmptyCorrectSetWithEmptyPick_IsKnew_BecauseTheExactSetRuleAppliesToTwoEmptySets() =>
        Assert.Equal(Grade.Knew, AutoGrading.Multi([], [], false));

    [Fact]
    public void FindBug_EmptyAcceptedSetMatchesAnEmptySelection() =>
        Assert.Equal(Grade.Knew, AutoGrading.FindBug([[]], [], false));

    [Fact]
    public void Multi_IsCaseSensitive() => Assert.Equal(Grade.DidntKnow, AutoGrading.Multi(["a"], ["A"], false));

    [Fact]
    public void Single_Correct_IsKnew() => Assert.Equal(Grade.Knew, AutoGrading.Single("b", "b", false));

    [Fact]
    public void Single_CorrectWithHint_IsPartial() => Assert.Equal(Grade.Partial, AutoGrading.Single("b", "b", usedHint: true));

    [Fact]
    public void Single_WrongWithHint_StaysDidntKnow() => Assert.Equal(Grade.DidntKnow, AutoGrading.Single("b", "a", true));

    [Theory]
    [InlineData("333", true)]
    [InlineData("  333\r\n", true)]
    [InlineData("3 3 3", false)]
    [InlineData("", false)]
    public void TypedOutput_NormalisesLikeTheValidator(string typed, bool knew) =>
        Assert.Equal(knew ? Grade.Knew : Grade.DidntKnow, AutoGrading.TypedOutput(["333"], typed, false));

    [Fact]
    public void TypedOutput_MultilineCrLf_MatchesLf() => Assert.Equal(Grade.Knew, AutoGrading.TypedOutput(["1\n2"], "1\r\n2", false));

    [Fact]
    public void TypedOutput_MatchesAnyAcceptedOutput() => Assert.Equal(Grade.Knew, AutoGrading.TypedOutput(["1", "2"], "2", false));

    [Fact]
    public void TypedOutput_WithHint_IsPartial() => Assert.Equal(Grade.Partial, AutoGrading.TypedOutput(["1"], "1", true));

    [Fact]
    public void FindBug_SelectionEqualToAnyAcceptedSet_IsKnew() => Assert.Equal(Grade.Knew, AutoGrading.FindBug([[7], [3, 4]], [4, 3], false));

    [Fact]
    public void FindBug_Superset_IsDidntKnow() => Assert.Equal(Grade.DidntKnow, AutoGrading.FindBug([[7]], [7, 8], false));

    [Fact]
    public void FindBug_WithHint_IsPartial() => Assert.Equal(Grade.Partial, AutoGrading.FindBug([[7]], [7], true));

    [Theory]
    [InlineData(Grade.Knew, false, Grade.Knew)]
    [InlineData(Grade.Knew, true, Grade.Partial)]
    [InlineData(Grade.Partial, true, Grade.Partial)]
    [InlineData(Grade.DidntKnow, true, Grade.DidntKnow)]
    public void CapForHint_CapsAtPartial(Grade grade, bool usedHint, Grade expected) =>
        Assert.Equal(expected, AutoGrading.CapForHint(grade, usedHint));

    [Fact]
    public void OutputText_Normalise_UnifiesLineEndingsAndTrims() => Assert.Equal("a\nb\nc", OutputText.Normalise(" a\r\nb\rc\n"));
}
