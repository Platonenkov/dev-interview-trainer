using Trainer.Domain.Ids;

namespace Trainer.Domain.Users;

/// <summary>Application user id: a server-generated ULID. The Telegram user id is a separate unique column.</summary>
public readonly record struct UserId(string Value)
{
    public static bool TryCreate(string? value, out UserId id)
    {
        if (UlidText.IsValid(value))
        {
            id = new UserId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
