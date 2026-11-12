using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trainer.Web.Seo;

/// <summary>Writes the schema.org <c>BreadcrumbList</c> JSON that goes into a JSON-LD script element.</summary>
public static class BreadcrumbJson
{
    // The default encoder escapes <, >, & and the apostrophe, so a title such as "</script>" cannot close the script element.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.Default };

    public static string Write(IReadOnlyList<BreadcrumbItem> items, Func<string, string> toAbsolute)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(toAbsolute);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("@context", "https://schema.org");
            writer.WriteString("@type", "BreadcrumbList");
            writer.WriteStartArray("itemListElement");
            for (int i = 0; i < items.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("@type", "ListItem");
                writer.WriteNumber("position", i + 1);
                writer.WriteString("name", items[i].Name);
                writer.WriteString("item", toAbsolute(items[i].PathAndQuery));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
