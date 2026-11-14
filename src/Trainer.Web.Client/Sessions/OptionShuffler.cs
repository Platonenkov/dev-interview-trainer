using Trainer.Contracts;

namespace Trainer.Web.Client.Sessions;

/// <summary>Option order is shuffled in every mode (spec 5.3), so position never gives the answer away.</summary>
public static class OptionShuffler
{
    /// <summary>A Fisher-Yates shuffle into a new list; <paramref name="options"/> is left as it is.</summary>
    public static IReadOnlyList<OptionDto> Shuffle(IReadOnlyList<OptionDto> options, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);

        OptionDto[] shuffled = [.. options];
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }
}
