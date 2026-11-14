namespace Trainer.Domain.Ids;

/// <summary>Client-generated ULID that makes an answer idempotent across outbox retries.</summary>
public readonly record struct ClientAnswerId(string Value)
{
    public static bool TryCreate(string? value, out ClientAnswerId id)
    {
        if (UlidText.IsValid(value))
        {
            id = new ClientAnswerId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
