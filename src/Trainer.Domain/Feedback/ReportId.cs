using Trainer.Domain.Ids;

namespace Trainer.Domain.Feedback;

/// <summary>Server-generated ULID of a question report.</summary>
public readonly record struct ReportId(string Value)
{
    public static bool TryCreate(string? value, out ReportId id)
    {
        if (UlidText.IsValid(value))
        {
            id = new ReportId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
