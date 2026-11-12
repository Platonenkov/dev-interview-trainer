namespace Trainer.Domain.Keys;

/// <summary>Identifier of a topic within a stack. Lowercase kebab-case, 1 to 80 characters.</summary>
public readonly record struct TopicKey(string Value)
{
    public static bool TryCreate(string? value, out TopicKey result)
    {
        if (KeyPattern.IsValid(value))
        {
            result = new TopicKey(value!);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value;
}
