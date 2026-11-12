namespace Trainer.Domain.Keys;

/// <summary>Identifier of a technology stack (the second URL segment). Lowercase kebab-case, 1 to 80 characters.</summary>
public readonly record struct StackKey(string Value)
{
    public static bool TryCreate(string? value, out StackKey result)
    {
        if (KeyPattern.IsValid(value))
        {
            result = new StackKey(value!);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value;
}
