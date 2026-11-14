namespace Trainer.Domain.Ids;

/// <summary>Client-generated ULID of a mock interview session.</summary>
public readonly record struct MockSessionId(string Value)
{
    public static bool TryCreate(string? value, out MockSessionId id)
    {
        if (UlidText.IsValid(value))
        {
            id = new MockSessionId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
