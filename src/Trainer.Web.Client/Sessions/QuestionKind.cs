using Trainer.Contracts;
using Trainer.Domain.Questions;

namespace Trainer.Web.Client.Sessions;

/// <summary>How the island asks and grades a question; the content type alone does not say it for <c>output</c>.</summary>
public enum QuestionKind
{
    SingleChoice,
    MultiChoice,
    OutputChoice,
    OutputTyped,
    FindBug,
    SelfAssessed,
}

public static class QuestionKinds
{
    /// <summary>
    /// <c>output</c> with options is <see cref="QuestionKind.OutputChoice"/>, without them <see cref="QuestionKind.OutputTyped"/>;
    /// <c>open</c> and <c>situation</c> are <see cref="QuestionKind.SelfAssessed"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The question type is not a known content key.</exception>
    public static QuestionKind Of(QuestionDto question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!QuestionTypeKeys.TryParse(question.Type, out QuestionType type))
        {
            throw new ArgumentException($"Unknown question type '{question.Type}'.", nameof(question));
        }

        return type switch
        {
            QuestionType.Single => QuestionKind.SingleChoice,
            QuestionType.Multi => QuestionKind.MultiChoice,
            QuestionType.Output => question.Options.Count > 0 ? QuestionKind.OutputChoice : QuestionKind.OutputTyped,
            QuestionType.FindBug => QuestionKind.FindBug,
            _ => QuestionKind.SelfAssessed,
        };
    }

    /// <summary>True for the kinds the user answers by picking options.</summary>
    public static bool IsChoice(this QuestionKind kind) =>
        kind is QuestionKind.SingleChoice or QuestionKind.MultiChoice or QuestionKind.OutputChoice;
}
