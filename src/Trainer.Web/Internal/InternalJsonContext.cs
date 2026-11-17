using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trainer.Web.Internal;

/// <summary>Source-generated serializer metadata (camelCase) for the internal endpoint bodies.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InternalImportDto))]
[JsonSerializable(typeof(InternalImportIssueDto))]
[JsonSerializable(typeof(InternalVersionDto))]
public sealed partial class InternalJsonContext : JsonSerializerContext;
