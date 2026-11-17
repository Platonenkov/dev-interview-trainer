namespace Trainer.Domain.Feedback;

/// <summary>A signed-in user's opinion of a question. The numbers are stored in the database; do not renumber.</summary>
public enum QuestionRating
{
    Dislike = -1,
    Like = 1,
}
