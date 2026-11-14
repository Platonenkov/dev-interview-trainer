using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Trainer.Application.Abstractions;
using Trainer.Application.Content;
using Trainer.Application.Users;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.Domain.Ids;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Content;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Users;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

/// <summary>
/// A private database with the valid content fixture imported, a fake clock and helpers to create users, for the
/// answer and mock-session tests.
/// </summary>
internal sealed class LearningDatabase : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private LearningDatabase(string connectionString, FakeTimeProvider clock, TrainerDbContext db, IReadOnlyList<QuestionId> questions)
    {
        ConnectionString = connectionString;
        Clock = clock;
        Db = db;
        Questions = questions;
    }

    public string ConnectionString { get; }

    public FakeTimeProvider Clock { get; }

    /// <summary>A context for arrange and assert steps; read with <c>AsNoTracking</c>.</summary>
    public TrainerDbContext Db { get; }

    /// <summary>The ids of the six imported questions, ordered.</summary>
    public IReadOnlyList<QuestionId> Questions { get; }

    public static async Task<LearningDatabase> CreateAsync(PostgresFixture postgres, CancellationToken ct)
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        FakeTimeProvider clock = new(Start);
        await using (TrainerDbContext importDb = PostgresFixture.CreateContext(connectionString))
        {
            ContentRenderer renderer = new();
            ContentImportService import = new(
                importDb,
                new ContentReader(),
                new ContentValidator(renderer, clock),
                renderer,
                new NoInvalidation(),
                clock,
                NullLogger<ContentImportService>.Instance);
            ContentImportResult result = await import.ImportAsync(ContentWorkspace.ValidFixtureRoot, ct);
            if (result.Status != ContentImportStatus.Imported)
            {
                throw new InvalidOperationException($"The content fixture was not imported: {result.Status}.");
            }
        }

        TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        List<string> ids = await db.Questions.AsNoTracking().OrderBy(q => q.Id).Select(q => q.Id).ToListAsync(ct);
        return new LearningDatabase(connectionString, clock, db, [.. ids.Select(id => new QuestionId(id))]);
    }

    public TrainerDbContext NewContext() => PostgresFixture.CreateContext(ConnectionString);

    public async Task<UserId> CreateUserAsync(long telegramUserId, CancellationToken ct)
    {
        await using TrainerDbContext db = NewContext();
        UserAccountService users = new(db, Options.Create(new AccessOptions()), new UlidGenerator(Clock), Clock);
        UserAccess access = await users.SignInAsync(new TelegramProfile(telegramUserId, "user" + telegramUserId, null), ct);
        return access.UserId;
    }

    public async Task RetireQuestionAsync(QuestionId question, CancellationToken ct)
    {
        DateTimeOffset now = Clock.GetUtcNow();
        await Db.Questions.Where(q => q.Id == question.Value).ExecuteUpdateAsync(s => s.SetProperty(q => q.RetiredAt, now), ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();

        // Every database has its own connection string and so its own pool. Idle pooled connections of finished tests
        // would otherwise add up past the server's max_connections.
        await using NpgsqlConnection connection = new(ConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    private sealed class NoInvalidation : IContentCacheInvalidator
    {
        public Task InvalidateAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
