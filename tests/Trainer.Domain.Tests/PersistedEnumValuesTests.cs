using Trainer.Domain.Grading;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Domain.Tests;

/// <summary>These numbers are stored in the database; changing one silently relabels existing rows.</summary>
public sealed class PersistedEnumValuesTests
{
    [Fact]
    public void QuestionType_NumericValuesArePinned()
    {
        Assert.Equal(
            [(QuestionType.Open, 1), (QuestionType.Single, 2), (QuestionType.Multi, 3), (QuestionType.Output, 4), (QuestionType.FindBug, 5), (QuestionType.Situation, 6)],
            Enum.GetValues<QuestionType>().Select(v => (v, (int)v)).OrderBy(p => p.Item2));
    }

    [Fact]
    public void GradingMode_NumericValuesArePinned()
    {
        Assert.Equal(
            [(GradingMode.Self, 1), (GradingMode.Auto, 2)],
            Enum.GetValues<GradingMode>().Select(v => (v, (int)v)).OrderBy(p => p.Item2));
    }

    [Fact]
    public void Level_NumericValuesArePinned()
    {
        Assert.Equal(
            [(Level.Junior, 1), (Level.Middle, 2), (Level.Senior, 3), (Level.Lead, 4), (Level.Architect, 5)],
            Enum.GetValues<Level>().Select(v => (v, (int)v)).OrderBy(p => p.Item2));
    }

    [Fact]
    public void Grade_NumericValuesArePinned()
    {
        Assert.Equal(
            [(Grade.DidntKnow, 0), (Grade.Partial, 1), (Grade.Knew, 2)],
            Enum.GetValues<Grade>().Select(v => (v, (int)v)).OrderBy(p => p.Item2));
    }

    [Fact]
    public void AnswerMode_NumericValuesArePinned()
    {
        Assert.Equal(
            [(AnswerMode.Practice, 1), (AnswerMode.Mock, 2), (AnswerMode.Review, 3)],
            Enum.GetValues<AnswerMode>().Select(v => (v, (int)v)).OrderBy(p => p.Item2));
    }
}
