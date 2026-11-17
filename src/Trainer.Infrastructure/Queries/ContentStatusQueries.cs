using Microsoft.EntityFrameworkCore;
using Trainer.Application.Content;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Infrastructure.Queries;

internal sealed class ContentStatusQueries(TrainerDbContext db) : IContentStatusQueries
{
    public Task<string?> GetActiveHashAsync(CancellationToken ct) =>
        db.ContentImports.AsNoTracking()
            .Where(i => i.IsActive)
            .Select(i => (string?)i.Hash)
            .SingleOrDefaultAsync(ct);
}
