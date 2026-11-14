using System.Net;
using Microsoft.EntityFrameworkCore;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using Trainer.Web.Caching;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>The key ring lives in PostgreSQL (ADR 0004), so cookies survive a restart and are shared by instances.</summary>
public sealed class DataProtectionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> KeyCountAsync(string connectionString)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        return await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM data_protection_keys").SingleAsync(Ct);
    }

    [Fact]
    public async Task DataProtectionKeys_ArePersistedInDatabase()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        string connectionString = await postgres.CreateDatabaseAsync();
        string sessionCookie;
        await using (SessionSite first = await SessionSite.StartAsync(postgres, connectionString: connectionString, telegram: telegram))
        {
            (HttpClient client, HttpResponseMessage callback) = await first.SignInAsync(new FakeTelegramIdentity(560001, "Key Keeper", null), Ct);
            using HttpClient disposableClient = client;
            using HttpResponseMessage signedIn = callback;
            sessionCookie = $"{AuthCookieNames.Session}={SessionCookieValue(signedIn)}";
        }

        Assert.True(await KeyCountAsync(connectionString) > 0, "No data protection key was stored.");

        await using SessionSite second = await SessionSite.StartAsync(postgres, connectionString: connectionString, telegram: telegram);
        using HttpClient bare = Bare(second.Factory);
        using HttpRequestMessage request = new(HttpMethod.Get, "/en");
        request.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage response = await bare.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">Key Keeper</a>", body, StringComparison.Ordinal);
    }
}
