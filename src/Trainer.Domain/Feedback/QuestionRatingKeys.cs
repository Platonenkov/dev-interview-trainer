namespace Trainer.Domain.Feedback;

/// <summary>Maps <see cref="QuestionRating"/> to its wire key and back. Keys travel only between our own client and server.</summary>
public static class QuestionRatingKeys
{
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined rating.</exception>
    public static string ToKey(QuestionRating rating) => rating switch
    {
        QuestionRating.Like => "like",
        QuestionRating.Dislike => "dislike",
        _ => throw new ArgumentOutOfRangeException(nameof(rating), rating, null),
    };

    /// <summary>Parses a key case-sensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out QuestionRating rating)
    {
        if (string.Equals(key, "like", StringComparison.Ordinal))
        {
            rating = QuestionRating.Like;
            return true;
        }

        if (string.Equals(key, "dislike", StringComparison.Ordinal))
        {
            rating = QuestionRating.Dislike;
            return true;
        }

        rating = default;
        return false;
    }
}
