using Trainer.Domain.Ids;

namespace Trainer.Domain.Questions;

/// <summary>Stable question identifier: an uppercase Crockford base32 ULID (see <see cref="UlidText.IsValid"/>).</summary>
public readonly record struct QuestionId(string Value)
{
    public static bool TryCreate(string? value, out QuestionId id)
    {
        if (UlidText.IsValid(value))
        {
            id = new QuestionId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
