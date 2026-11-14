using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Trainer.Application.Admin;
using Trainer.Application.Users;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Admin;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Users;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class AdminQueriesTests(PostgresFixture postgres)
{
    private const long AdminTelegramId = 800_000_001L;
    private static readonly UlidGenerator Ids = new(TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AdminQueries CreateQueries(TrainerDbContext db, LearningDatabase data) =>
        new(db, Options.Create(new AccessOptions { AdminTelegramIds = [AdminTelegramId] }), data.Clock);

    private static UserAccountService CreateUsers(TrainerDbContext db, LearningDatabase data) =>
        new(db, Options.Create(new AccessOptions()), new UlidGenerator(data.Clock), data.Clock);

    private static async Task<UserId> SignInAsync(LearningDatabase data, long telegramId, string? username, string? name)
    {
        await using TrainerDbContext db = data.NewContext();
        UserAccess access = await CreateUsers(db, data).SignInAsync(new TelegramProfile(telegramId, username, name), Ct);
        return access.UserId;
    }

    private static async Task AddAnswerAsync(LearningDatabase data, UserId user, DateTimeOffset receivedAt)
    {
        data.Db.Answers.Add(new AnswerEntity
        {
            Id = Ids.Next(),
            ClientAnswerId = Ids.Next(),
            UserId = user.Value,
            QuestionId = data.Questions[0].Value,
            Grade = Grade.Knew,
            Mode = AnswerMode.Practice,
            AnsweredAt = receivedAt,
            ReceivedAt = receivedAt,
        });
        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Overview_ReportsActiveImportAndCounts()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId first = await SignInAsync(data, 1001, "first", "First");
        UserId second = await SignInAsync(data, 1002, "second", "Second");
        await SignInAsync(data, 1003, "third", "Third");
        await CreateUsers(data.Db, data).SetBlockedAsync(second, true, Ct);
        await AddAnswerAsync(data, first, data.Clock.GetUtcNow().AddHours(-1));
        await AddAnswerAsync(data, first, data.Clock.GetUtcNow().AddHours(-23));
        await AddAnswerAsync(data, second, data.Clock.GetUtcNow().AddHours(-25));
        data.Db.ContentImports.Add(new ContentImportEntity
        {
            Hash = new string('a', 64),
            SchemaVersion = 1,
            RenderVersion = 2,
            ImportedAt = data.Clock.GetUtcNow().AddDays(-3),
            QuestionCount = 4,
            IsActive = false,
        });
        await data.Db.SaveChangesAsync(Ct);
        await using TrainerDbContext db = data.NewContext();

        AdminOverview overview = await CreateQueries(db, data).GetOverviewAsync(Ct);

        Assert.True(overview.DatabaseReachable);
        Assert.Equal(3, overview.UserCount);
        Assert.Equal(1, overview.BlockedCount);
        Assert.Equal(2, overview.AnswersLast24Hours);
        Assert.Equal(2, overview.RecentImports.Count);
        ImportRow active = overview.RecentImports[0];
        Assert.True(active.IsActive);
        Assert.Equal(6, active.QuestionCount);
        Assert.Equal(64, active.Hash.Length);
        ImportRow older = overview.RecentImports[1];
        Assert.False(older.IsActive);
        Assert.Equal(new string('a', 64), older.Hash);
        Assert.Equal(2, older.RenderVersion);
        Assert.Equal(4, older.QuestionCount);
    }

    [Fact]
    public async Task AnswersByReceivedAt_AreIndexed()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);

        int indexes = await data.Db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE tablename = 'answers' AND indexdef LIKE '%(received_at)%'")
            .SingleAsync(Ct);

        Assert.Equal(1, indexes);
    }

    [Fact]
    public async Task Overview_ListsOnlyTheLatestImports()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        for (int i = 0; i < AdminLimits.RecentImportCount + 3; i++)
        {
            data.Db.ContentImports.Add(new ContentImportEntity
            {
                Hash = i.ToString("D64", CultureInfo.InvariantCulture),
                SchemaVersion = 1,
                RenderVersion = 1,
                ImportedAt = data.Clock.GetUtcNow().AddDays(-1 - i),
                QuestionCount = i,
                IsActive = false,
            });
        }

        await data.Db.SaveChangesAsync(Ct);
        await using TrainerDbContext db = data.NewContext();

        AdminOverview overview = await CreateQueries(db, data).GetOverviewAsync(Ct);

        Assert.Equal(AdminLimits.RecentImportCount, overview.RecentImports.Count);
        Assert.True(overview.RecentImports[0].IsActive);
        Assert.Equal(
            overview.RecentImports.OrderByDescending(r => r.ImportedAt).Select(r => r.Hash),
            overview.RecentImports.Select(r => r.Hash));
    }

    [Fact]
    public async Task Overview_WhenTheDatabaseIsUnreachable_ReportsItAndNothingElse()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        string broken = new NpgsqlConnectionStringBuilder(data.ConnectionString) { Port = 1, Timeout = 2, Pooling = false }.ConnectionString;
        await using TrainerDbContext db = PostgresFixture.CreateContext(broken);

        AdminOverview overview = await CreateQueries(db, data).GetOverviewAsync(Ct);

        Assert.False(overview.DatabaseReachable);
        Assert.Empty(overview.RecentImports);
        Assert.Equal(0, overview.UserCount);
    }

    [Fact]
    public async Task Users_SearchByUsernameAndId()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await SignInAsync(data, 2001, "AliceCoder", "Alice Aardvark");
        await SignInAsync(data, 2002, "bob_b", "Bob Badger");
        await SignInAsync(data, AdminTelegramId, null, null);
        await using TrainerDbContext db = data.NewContext();
        AdminQueries queries = CreateQueries(db, data);

        UserPage byUsername = await queries.GetUsersAsync("alicecoder", 1, Ct);
        UserPage byDisplayName = await queries.GetUsersAsync("  BADGER ", 1, Ct);
        UserPage byId = await queries.GetUsersAsync("2002", 1, Ct);
        UserPage unknownId = await queries.GetUsersAsync("999999", 1, Ct);
        UserPage everyone = await queries.GetUsersAsync("  ", 1, Ct);

        Assert.Equal(2001, Assert.Single(byUsername.Rows).TelegramUserId);
        Assert.Equal(2002, Assert.Single(byDisplayName.Rows).TelegramUserId);
        AdminUserRow bob = Assert.Single(byId.Rows);
        Assert.Equal("Bob Badger", bob.DisplayName);
        Assert.Equal("bob_b", bob.Username);
        Assert.Equal(1, byId.Total);
        Assert.Empty(unknownId.Rows);
        Assert.Equal(0, unknownId.Total);
        Assert.Equal(1, unknownId.PageCount);
        Assert.Equal(3, everyone.Total);
    }

    [Fact]
    public async Task Users_SearchFoldsCyrillicCaseWhateverTheDatabaseCtype()
    {
        string connectionString = await CCtypeDatabase.CreateAsync(postgres, Ct);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        await using (TrainerDbContext seed = PostgresFixture.CreateContext(connectionString))
        {
            UserAccountService users = new(seed, Options.Create(new AccessOptions()), new UlidGenerator(clock), clock);
            await users.SignInAsync(new TelegramProfile(3001, "ivan_dev", "Иван Петров"), Ct);
            await users.SignInAsync(new TelegramProfile(3002, "ОЛЕГ", "Oleg"), Ct);
        }

        await using TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        AdminQueries queries = new(db, Options.Create(new AccessOptions()), clock);

        UserPage byName = await queries.GetUsersAsync("иван", 1, Ct);
        UserPage byUsername = await queries.GetUsersAsync("олег", 1, Ct);

        Assert.Equal(3001, Assert.Single(byName.Rows).TelegramUserId);
        Assert.Equal(3002, Assert.Single(byUsername.Rows).TelegramUserId);
    }

    [Fact]
    public async Task Users_IdSearchIsExactNotAPrefix()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await SignInAsync(data, 2001, null, null);
        await SignInAsync(data, 20012, null, null);
        await using TrainerDbContext db = data.NewContext();

        UserPage page = await CreateQueries(db, data).GetUsersAsync("2001", 1, Ct);

        Assert.Equal(2001, Assert.Single(page.Rows).TelegramUserId);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    [InlineData("a%")]
    [InlineData("%a")]
    [InlineData("a_c")]
    public async Task Users_SearchEscapesWildcards(string search)
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await SignInAsync(data, 3001, "plain", "Plain Person");
        await SignInAsync(data, 3002, "abc", "Alphabet");
        await SignInAsync(data, 3003, "100%_done", "Back\\slash");
        await using TrainerDbContext db = data.NewContext();

        UserPage page = await CreateQueries(db, data).GetUsersAsync(search, 1, Ct);

        // A wildcard would match several users; a literal match can only hit the one name that contains the characters.
        Assert.True(page.Total <= 1, $"'{search}' matched {page.Total} users, so it was treated as a pattern.");
        if (search is "%" or "_" or "\\")
        {
            Assert.Equal(3003, Assert.Single(page.Rows).TelegramUserId);
        }
    }

    [Fact]
    public async Task Users_Pages()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        int total = AdminLimits.UsersPerPage + 5;
        for (int i = 0; i < total; i++)
        {
            await SignInAsync(data, 4000 + i, "u" + i, null);
            data.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await using TrainerDbContext db = data.NewContext();
        AdminQueries queries = CreateQueries(db, data);

        UserPage first = await queries.GetUsersAsync(null, 1, Ct);
        UserPage second = await queries.GetUsersAsync(null, 2, Ct);
        UserPage beyond = await queries.GetUsersAsync(null, 99, Ct);
        UserPage below = await queries.GetUsersAsync(null, -3, Ct);

        Assert.Equal(AdminLimits.UsersPerPage, first.Rows.Count);
        Assert.Equal(5, second.Rows.Count);
        Assert.Equal(total, first.Total);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, second.Page);
        Assert.Equal(4000 + total - 1, first.Rows[0].TelegramUserId);
        Assert.Equal(4000, second.Rows[^1].TelegramUserId);
        Assert.Empty(first.Rows.Select(r => r.Id).Intersect(second.Rows.Select(r => r.Id)));
        Assert.Equal(2, beyond.Page);
        Assert.Equal(second.Rows.Select(r => r.Id), beyond.Rows.Select(r => r.Id));
        Assert.Equal(1, below.Page);
    }

    [Fact]
    public async Task Users_ReportRolesBlockedFlagAndTimes()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId editor = await SignInAsync(data, 5001, "ed", "Ed");
        await SignInAsync(data, AdminTelegramId, "boss", "Boss");
        UserId blocked = await SignInAsync(data, 5003, "bad", "Bad");
        UserAccountService service = CreateUsers(data.Db, data);
        await service.SetRolesAsync(editor, [Roles.Editor], Ct);
        await service.SetBlockedAsync(blocked, true, Ct);
        await using TrainerDbContext db = data.NewContext();

        UserPage page = await CreateQueries(db, data).GetUsersAsync(null, 1, Ct);

        AdminUserRow editorRow = page.Rows.Single(r => r.TelegramUserId == 5001);
        Assert.Equal([Roles.Editor], editorRow.Roles);
        Assert.False(editorRow.IsBlocked);
        Assert.Equal([Roles.Admin], page.Rows.Single(r => r.TelegramUserId == AdminTelegramId).Roles);
        AdminUserRow blockedRow = page.Rows.Single(r => r.TelegramUserId == 5003);
        Assert.True(blockedRow.IsBlocked);
        Assert.Empty(blockedRow.Roles);
        Assert.Equal(LearningDatabase.Start, blockedRow.CreatedAt);
        Assert.Equal(LearningDatabase.Start, blockedRow.LastSignInAt);
    }
}
