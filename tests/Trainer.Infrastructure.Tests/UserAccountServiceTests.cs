using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Trainer.Application.Users;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Users;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class UserAccountServiceTests(PostgresFixture postgres)
{
    private const long TelegramId = 100_200_300_400L;
    private const long AdminTelegramId = 7_000_000_001L;
    private const string UnknownUserId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    private static readonly DateTimeOffset Start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Harness(string ConnectionString, FakeTimeProvider Clock, TrainerDbContext Db, UserAccountService Service)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Harness> CreateAsync(params long[] adminIds)
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        FakeTimeProvider clock = new(Start);
        TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        return new Harness(connectionString, clock, db, CreateService(db, clock, adminIds));
    }

    private static UserAccountService CreateService(TrainerDbContext db, FakeTimeProvider clock, long[] adminIds) =>
        new(db, Options.Create(new AccessOptions { AdminTelegramIds = adminIds }), new UlidGenerator(clock), clock);

    private static TelegramProfile Profile(long id = TelegramId, string? username = "alice", string? name = "Alice A.") =>
        new(id, username, name);

    [Fact]
    public async Task SignIn_NewUser_CreatesRowWithUlidAndStamp()
    {
        await using Harness h = await CreateAsync();

        UserAccess access = await h.Service.SignInAsync(Profile(), Ct);

        Assert.True(UlidText.IsValid(access.UserId.Value));
        Assert.Equal(32, access.SecurityStamp.Length);
        Assert.Matches("^[0-9a-f]{32}$", access.SecurityStamp);
        Assert.False(access.IsBlocked);
        Assert.Empty(access.Roles);
        Assert.Equal("Alice A.", access.DisplayName);
        Assert.Equal("alice", access.Username);
        Assert.Null(access.PreferredCulture);

        UserEntity row = await h.Db.Users.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(access.UserId.Value, row.Id);
        Assert.Equal(TelegramId, row.TelegramUserId);
        Assert.Equal(Start, row.CreatedAt);
        Assert.Equal(Start, row.LastSignInAt);
        Assert.Equal((short)0, row.UtcOffsetMinutes);
    }

    [Fact]
    public async Task SignIn_ExistingUser_UpdatesProfileKeepsStampAndId()
    {
        await using Harness h = await CreateAsync();
        UserAccess first = await h.Service.SignInAsync(Profile(), Ct);
        h.Clock.Advance(TimeSpan.FromHours(3));

        UserAccess second = await h.Service.SignInAsync(Profile(username: "alice2", name: "Alice B."), Ct);

        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(first.SecurityStamp, second.SecurityStamp);
        Assert.Equal("alice2", second.Username);
        Assert.Equal("Alice B.", second.DisplayName);
        UserEntity row = await h.Db.Users.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Start, row.CreatedAt);
        Assert.Equal(Start.AddHours(3), row.LastSignInAt);
    }

    [Fact]
    public async Task SignIn_ConcurrentFirstSignIns_CreateOneUser()
    {
        await using Harness h = await CreateAsync();
        await using TrainerDbContext secondDb = PostgresFixture.CreateContext(h.ConnectionString);
        UserAccountService second = CreateService(secondDb, h.Clock, []);
        using Barrier barrier = new(2);

        Task<UserAccess> a = Task.Run(() =>
        {
            barrier.SignalAndWait(Ct);
            return h.Service.SignInAsync(Profile(), Ct);
        }, Ct);
        Task<UserAccess> b = Task.Run(() =>
        {
            barrier.SignalAndWait(Ct);
            return second.SignInAsync(Profile(), Ct);
        }, Ct);
        UserAccess[] results = await Task.WhenAll(a, b);

        Assert.Equal(results[0].UserId, results[1].UserId);
        Assert.Equal(1, await h.Db.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task SignIn_ConfiguredAdmin_GetsAdminRoleWithoutRow()
    {
        await using Harness h = await CreateAsync(AdminTelegramId);

        UserAccess access = await h.Service.SignInAsync(Profile(AdminTelegramId), Ct);

        Assert.Equal([Roles.Admin], access.Roles);
        Assert.Equal(0, await h.Db.UserRoles.CountAsync(Ct));
    }

    [Fact]
    public async Task SignIn_BlockedUser_ReturnsBlockedAccess()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);
        await h.Service.SetBlockedAsync(created.UserId, true, Ct);

        UserAccess access = await h.Service.SignInAsync(Profile(), Ct);

        Assert.True(access.IsBlocked);
    }

    [Fact]
    public async Task SetBlocked_RotatesStamp()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);

        BlockResult result = await h.Service.SetBlockedAsync(created.UserId, true, Ct);

        Assert.Equal(BlockResult.Updated, result);
        UserAccess? after = await h.Service.GetAccessAsync(TelegramId, Ct);
        Assert.NotNull(after);
        Assert.True(after.IsBlocked);
        Assert.NotEqual(created.SecurityStamp, after.SecurityStamp);
        Assert.Matches("^[0-9a-f]{32}$", after.SecurityStamp);
    }

    [Fact]
    public async Task SetBlocked_ConfiguredAdmin_IsProtected()
    {
        await using Harness h = await CreateAsync(AdminTelegramId);
        UserAccess admin = await h.Service.SignInAsync(Profile(AdminTelegramId), Ct);

        BlockResult result = await h.Service.SetBlockedAsync(admin.UserId, true, Ct);

        Assert.Equal(BlockResult.ProtectedAdmin, result);
        UserAccess? after = await h.Service.GetAccessAsync(AdminTelegramId, Ct);
        Assert.NotNull(after);
        Assert.False(after.IsBlocked);
        Assert.Equal(admin.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task SetBlocked_UnknownUser_NotFound()
    {
        await using Harness h = await CreateAsync();

        BlockResult result = await h.Service.SetBlockedAsync(new UserId(UnknownUserId), true, Ct);

        Assert.Equal(BlockResult.NotFound, result);
    }

    [Fact]
    public async Task SetRoles_ReplacesRowsAndRotatesStamp()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);

        await h.Service.SetRolesAsync(created.UserId, [Roles.Editor, Roles.Editor], Ct);
        UserAccess? editor = await h.Service.GetAccessAsync(TelegramId, Ct);
        await h.Service.SetRolesAsync(created.UserId, [], Ct);
        UserAccess? none = await h.Service.GetAccessAsync(TelegramId, Ct);

        Assert.NotNull(editor);
        Assert.NotNull(none);
        Assert.Equal([Roles.Editor], editor.Roles);
        Assert.Empty(none.Roles);
        Assert.NotEqual(created.SecurityStamp, editor.SecurityStamp);
        Assert.NotEqual(editor.SecurityStamp, none.SecurityStamp);
        Assert.Equal(0, await h.Db.UserRoles.CountAsync(Ct));
    }

    [Fact]
    public async Task SetRoles_Admin_ThrowsAndStoresNothing()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);
        await h.Service.SetRolesAsync(created.UserId, [Roles.Editor], Ct);

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.SetRolesAsync(created.UserId, [Roles.Editor, Roles.Admin], Ct));

        UserAccess? after = await h.Service.GetAccessAsync(TelegramId, Ct);
        Assert.NotNull(after);
        Assert.Equal([Roles.Editor], after.Roles);
    }

    [Fact]
    public async Task GetAccess_AdminIdRemovedFromConfig_LosesAdmin()
    {
        await using Harness h = await CreateAsync(AdminTelegramId);
        await h.Service.SignInAsync(Profile(AdminTelegramId), Ct);
        await using TrainerDbContext restartedDb = PostgresFixture.CreateContext(h.ConnectionString);
        UserAccountService restarted = CreateService(restartedDb, h.Clock, []);

        UserAccess? before = await h.Service.GetAccessAsync(AdminTelegramId, Ct);
        UserAccess? after = await restarted.GetAccessAsync(AdminTelegramId, Ct);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal([Roles.Admin], before.Roles);
        Assert.Empty(after.Roles);
    }

    [Fact]
    public async Task SignIn_ProfileWithoutNames_KeepsKnownNames()
    {
        await using Harness h = await CreateAsync();
        await h.Service.SignInAsync(Profile(), Ct);

        UserAccess second = await h.Service.SignInAsync(Profile(username: null, name: null), Ct);

        Assert.Equal("alice", second.Username);
        Assert.Equal("Alice A.", second.DisplayName);
    }

    [Fact]
    public async Task SetBlocked_UnblockRotatesStampAndClearsFlag()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);
        await h.Service.SetBlockedAsync(created.UserId, true, Ct);
        UserAccess? blocked = await h.Service.GetAccessAsync(TelegramId, Ct);

        BlockResult result = await h.Service.SetBlockedAsync(created.UserId, false, Ct);

        Assert.Equal(BlockResult.Updated, result);
        UserAccess? after = await h.Service.GetAccessAsync(TelegramId, Ct);
        Assert.NotNull(blocked);
        Assert.NotNull(after);
        Assert.False(after.IsBlocked);
        Assert.NotEqual(blocked.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task SetBlocked_AdminBlockedBeforeBeingConfigured_CanBeUnblockedButNotBlockedAgain()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(AdminTelegramId), Ct);
        await h.Service.SetBlockedAsync(created.UserId, true, Ct);
        await using TrainerDbContext restartedDb = PostgresFixture.CreateContext(h.ConnectionString);
        UserAccountService restarted = CreateService(restartedDb, h.Clock, [AdminTelegramId]);

        BlockResult unblock = await restarted.SetBlockedAsync(created.UserId, false, Ct);
        BlockResult block = await restarted.SetBlockedAsync(created.UserId, true, Ct);

        Assert.Equal(BlockResult.Updated, unblock);
        Assert.Equal(BlockResult.ProtectedAdmin, block);
        UserAccess? after = await restarted.GetAccessAsync(AdminTelegramId, Ct);
        Assert.NotNull(after);
        Assert.False(after.IsBlocked);
    }

    [Fact]
    public async Task SetRoles_UnknownRole_Throws()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.SetRolesAsync(created.UserId, ["Superuser"], Ct));

        Assert.Equal(0, await h.Db.UserRoles.CountAsync(Ct));
    }

    [Fact]
    public async Task SetRoles_UnknownUser_Throws()
    {
        await using Harness h = await CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.SetRolesAsync(new UserId(UnknownUserId), [Roles.Editor], Ct));
    }

    [Fact]
    public async Task SetPreferredCulture_UnknownUser_Throws()
    {
        await using Harness h = await CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.SetPreferredCultureAsync(new UserId(UnknownUserId), new CultureKey("ru"), Ct));
    }

    [Fact]
    public async Task GetAccess_UnknownTelegramId_ReturnsNull()
    {
        await using Harness h = await CreateAsync();

        Assert.Null(await h.Service.GetAccessAsync(TelegramId, Ct));
    }

    [Fact]
    public async Task SetPreferredCulture_IsReturnedByGetAccess()
    {
        await using Harness h = await CreateAsync();
        UserAccess created = await h.Service.SignInAsync(Profile(), Ct);

        await h.Service.SetPreferredCultureAsync(created.UserId, new CultureKey("ru"), Ct);

        UserAccess? after = await h.Service.GetAccessAsync(TelegramId, Ct);
        Assert.NotNull(after);
        Assert.Equal(new CultureKey("ru"), after.PreferredCulture);
        Assert.Equal(created.SecurityStamp, after.SecurityStamp);
    }
}
