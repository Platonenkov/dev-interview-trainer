using System.Globalization;

namespace Trainer.Web.Auth;

/// <summary>Reads <c>App:AdminTelegramIds</c> once at startup.</summary>
public static class AdminTelegramIds
{
    public const string Key = "App:AdminTelegramIds";

    /// <summary>
    /// Accepts an array section (<c>App__AdminTelegramIds__0=1</c>) or one comma-separated value
    /// (<c>App__AdminTelegramIds=1,2</c>, the form the deployment renders). Missing or empty means no admins.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is not a positive 64-bit integer.</exception>
    public static IReadOnlyList<long> Parse(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(Key);
        IEnumerable<string?> raw = section.Value is { } single
            ? single.Split(',')
            : section.GetChildren().Select(child => child.Value);

        List<long> ids = [];
        foreach (string? entry in raw)
        {
            string trimmed = entry?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 && section.Value is not null)
            {
                continue;
            }

            if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            {
                throw new InvalidOperationException($"{Key} contains '{entry}', which is not a positive Telegram user id.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
