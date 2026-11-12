namespace Trainer.Domain.Keys;

/// <summary>URL slug of a question, unique within its stack. Lowercase kebab-case, 1 to 80 characters.</summary>
public readonly record struct Slug(string Value)
{
    public static bool TryCreate(string? value, out Slug result)
    {
        if (KeyPattern.IsValid(value))
        {
            result = new Slug(value!);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value;
}
