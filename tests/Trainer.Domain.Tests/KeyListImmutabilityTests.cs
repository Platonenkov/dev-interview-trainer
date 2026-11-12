using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Domain.Tests;

public sealed class KeyListImmutabilityTests
{
    [Fact]
    public void Supported_IsNotAnArrayAndRejectsMutation() => AssertReadOnly(CultureKey.Supported, new CultureKey("en"));

    [Fact]
    public void LevelAll_IsNotAnArrayAndRejectsMutation() => AssertReadOnly(LevelKeys.All, Level.Junior);

    [Fact]
    public void GradingModeAll_IsNotAnArrayAndRejectsMutation() => AssertReadOnly(GradingModeKeys.All, GradingMode.Self);

    [Fact]
    public void QuestionTypeAll_IsNotAnArrayAndRejectsMutation() => AssertReadOnly(QuestionTypeKeys.All, QuestionType.Open);

    private static void AssertReadOnly<T>(IReadOnlyList<T> list, T replacement)
    {
        Assert.False(list is T[]);
        IList<T> asList = Assert.IsAssignableFrom<IList<T>>(list);
        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList[0] = replacement);
    }
}
