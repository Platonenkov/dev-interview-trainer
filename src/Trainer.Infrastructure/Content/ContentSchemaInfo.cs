using Trainer.Application.Content;
using Trainer.Content.Schema;

namespace Trainer.Infrastructure.Content;

internal sealed class ContentSchemaInfo : IContentSchemaInfo
{
    public int MinSupported => ContentSchema.MinSupported;

    public int MaxSupported => ContentSchema.MaxSupported;

    public int RenderVersion => ContentSchema.RenderVersion;
}
