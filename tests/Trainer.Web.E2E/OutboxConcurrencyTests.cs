using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace Trainer.Web.E2E;

/// <summary>
/// The real <c>outbox.js</c> in two tabs of one browser context (they share <c>localStorage</c> and Web Locks): every
/// read-modify-write is serialised by the per-key locks, so concurrent appends, removes and moves never lose or
/// duplicate an entry, and opposite moves never deadlock.
/// </summary>
[Trait("Category", "E2E")]
public sealed class OutboxConcurrencyTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string PracticePath = "/en/dotnet/practice";
    private const string UserKey = "dit.outbox.v1.u.990200001";
    private const string AnonKey = "dit.outbox.v1.anon";
    private const string PendingKey = "dit.mock.v1.pending.990200001";

    // Runs a list of ops one after another in the page and fails on the first refused one.
    private const string RunOps =
        """
        async (ops) => {
          const results = [];
          for (const op of ops) {
            const result = JSON.parse(await window.ditStore.mutate(JSON.stringify(op)));
            if (!result.ok) { throw new Error(JSON.stringify(op) + ' -> ' + JSON.stringify(result)); }
            results.push(result);
          }
          return results.length;
        }
        """;

    // Waits for a start time shared by both tabs, then starts every op at once, so the lock requests of the two tabs
    // interleave instead of one tab finishing before the other begins.
    private const string RunOpsConcurrently =
        """
        async ([ops, startAt]) => {
          await new Promise(resolve => setTimeout(resolve, Math.max(0, startAt - (performance.timeOrigin + performance.now()))));
          const results = await Promise.all(ops.map(op => window.ditStore.mutate(JSON.stringify(op)).then(JSON.parse)));
          const refused = results.find(r => !r.ok);
          if (refused) { throw new Error(JSON.stringify(refused)); }
          return results.length;
        }
        """;

    private const string MutateOnce = "async (op) => await window.ditStore.mutate(JSON.stringify(op))";

    private async Task<IBrowserContext> NewContextAsync() =>
        await host.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.BaseAddress.ToString(), Locale = "en-US" });

    // Waits for the island by default: the WebAssembly runtime writes its own localStorage entries while it boots
    // (blazor-resource-hash:*), and a test must not race with them.
    private async Task<IPage> OpenAsync(IBrowserContext context, bool waitForIsland = true)
    {
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath);
        await tab.WaitForFunctionAsync("() => !!window.ditStore", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        if (waitForIsland)
        {
            await tab.Locator(".island-root[data-island-ready='true']").WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        }

        return tab;
    }

    // Only the app's own keys: the framework keeps entries of its own in the same origin.
    private const string AppEntries = "() => JSON.stringify(Object.entries(localStorage).filter(([k]) => k.startsWith('dit.')).sort())";

    private static readonly string[] AckIds = ["a"];
    private static readonly string[] ClearIds = ["b"];

    private static object Entry(string id) => new
    {
        clientAnswerId = id,
        questionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA",
        grade = "knew",
        usedHint = false,
        mode = "practice",
        answeredAt = "2026-11-12T10:00:00+00:00",
    };

    private static object Append(string key, string id) => new { op = "append", key, value = new[] { Entry(id) } };

    private static object[] Appends(string key, string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => Append(key, prefix + i))];

    private static async Task<string[]> IdsAsync(IPage tab, string key, string list = "answers", string id = "clientAnswerId")
    {
        string? raw = await tab.EvaluateAsync<string?>("key => localStorage.getItem(key)", key);
        if (raw is null)
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(raw);
        return [.. doc.RootElement.GetProperty(list).EnumerateArray().Select(e => e.GetProperty(id).GetString()!)];
    }

    private static async Task<JsonElement> MutateAsync(IPage tab, object op)
    {
        string json = await tab.EvaluateAsync<string>(MutateOnce, op);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Outbox_ConcurrentAppendsFromTwoTabs_LoseNothing()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        IPage second = await OpenAsync(context);

        await Task.WhenAll(
            first.EvaluateAsync<int>(RunOps, Appends(UserKey, "first-", 200)),
            second.EvaluateAsync<int>(RunOps, Appends(UserKey, "second-", 200)));

        string[] ids = await IdsAsync(first, UserKey);
        Assert.Equal(400, ids.Length);
        Assert.Equal(400, ids.Distinct(StringComparer.Ordinal).Count());
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task Outbox_AppendDuringRemove_LosesNothing()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        IPage second = await OpenAsync(context);
        await first.EvaluateAsync<int>(RunOps, Appends(UserKey, "acked-", 100));
        object[] removes = [.. Enumerable.Range(0, 100).Select(i => new { op = "remove", key = UserKey, ids = new[] { "acked-" + i } })];

        await Task.WhenAll(
            first.EvaluateAsync<int>(RunOps, removes),
            second.EvaluateAsync<int>(RunOps, Appends(UserKey, "new-", 100)));

        string[] ids = await IdsAsync(first, UserKey);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => "new-" + i).Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task Outbox_OppositeMovesInTwoTabs_DoNotDeadlock()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        IPage second = await OpenAsync(context);
        await first.EvaluateAsync<int>(RunOps, Appends(AnonKey, "anon-", 50));
        await first.EvaluateAsync<int>(RunOps, Appends(UserKey, "user-", 50));
        object[] toUser = [.. Enumerable.Repeat<object>(new { op = "move", key = AnonKey, to = UserKey }, 100)];
        object[] toAnon = [.. Enumerable.Repeat<object>(new { op = "move", key = UserKey, to = AnonKey }, 100)];
        double startAt = await first.EvaluateAsync<double>("() => performance.timeOrigin + performance.now() + 300");

        Stopwatch elapsed = Stopwatch.StartNew();
        // Without the sorted lock order this deadlocks: a move holds its first key while the opposite move holds the other.
        Task both = Task.WhenAll(
            first.EvaluateAsync<int>(RunOpsConcurrently, new object[] { toUser, startAt }),
            second.EvaluateAsync<int>(RunOpsConcurrently, new object[] { toAnon, startAt }));
        Task finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2.3), TestContext.Current.CancellationToken));

        Assert.True(finished == both, $"Opposite moves did not finish within 2 s of their shared start ({elapsed.ElapsedMilliseconds} ms).");
        await both;
        string[] anon = await IdsAsync(first, AnonKey);
        string[] user = await IdsAsync(first, UserKey);
        Assert.Empty(anon.Intersect(user, StringComparer.Ordinal));
        Assert.Equal(100, anon.Concat(user).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(100, anon.Length + user.Length);
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task Outbox_QuotaExceeded_LeavesStoredValueIntact()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await tab.EvaluateAsync<int>(RunOps, Appends(UserKey, "kept-", 3));
        string? before = await tab.EvaluateAsync<string?>("key => localStorage.getItem(key)", UserKey);
        await tab.EvaluateAsync(
            """
            () => {
              let i = 0;
              for (let size = 1 << 20; size >= 16; ) {
                try { localStorage.setItem('filler-' + i++, 'x'.repeat(size)); } catch (e) { size >>= 1; }
              }
            }
            """);

        object big = new { op = "append", key = UserKey, value = new[] { new { clientAnswerId = "big", note = new string('y', 64 * 1024) } } };
        JsonElement result = await MutateAsync(tab, big);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("persisted").GetBoolean());
        Assert.Equal("quota", result.GetProperty("error").GetString());
        Assert.Equal(before, await tab.EvaluateAsync<string?>("key => localStorage.getItem(key)", UserKey));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Outbox_WithoutWebLocks_RefusesAndNeverWrites()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync("delete Navigator.prototype.locks;");
        // The island boots first: it publishes its owner under dit.account (no lock needed) and tries its own writes.
        IPage tab = await OpenAsync(context);
        Assert.True(await tab.EvaluateAsync<bool>("() => navigator.locks === undefined"));

        JsonElement appended = await MutateAsync(tab, Append(UserKey, "a"));
        JsonElement moved = await MutateAsync(tab, new { op = "move", key = AnonKey, to = UserKey });

        Assert.Equal("no-locks", appended.GetProperty("error").GetString());
        Assert.False(appended.GetProperty("ok").GetBoolean());
        Assert.Equal("no-locks", moved.GetProperty("error").GetString());
        // dit.account is the cross-tab hand-over, written without a lock by design; nothing else may exist.
        string[] keys = await tab.EvaluateAsync<string[]>("() => Object.keys(localStorage).filter(k => k.startsWith('dit.'))");
        Assert.Equal(["dit.account"], keys);
        // "owner|unix ms": the time lets a page tell a change made while it loaded from a stale value.
        Assert.Matches(@"^anon\|[0-9]+$", await tab.EvaluateAsync<string?>("() => localStorage.getItem('dit.account')"));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Move_CrashBetweenWrites_NextMoveCompletesWithoutDuplicates()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await tab.EvaluateAsync<int>(RunOps, Appends(AnonKey, "anon-", 3));
        await tab.EvaluateAsync<int>(RunOps, Appends(UserKey, "user-", 1));
        await tab.EvaluateAsync(
            """
            (key) => {
              const setItem = Storage.prototype.setItem;
              const removeItem = Storage.prototype.removeItem;
              let armed = true;
              const fail = (k) => { if (armed && k === key) { armed = false; throw new DOMException('injected', 'QuotaExceededError'); } };
              Storage.prototype.setItem = function (k, v) { fail(k); return setItem.call(this, k, v); };
              Storage.prototype.removeItem = function (k) { fail(k); return removeItem.call(this, k); };
            }
            """,
            AnonKey);

        JsonElement crashed = await MutateAsync(tab, new { op = "move", key = AnonKey, to = UserKey });

        Assert.False(crashed.GetProperty("ok").GetBoolean());
        Assert.True(crashed.GetProperty("persisted").GetBoolean());
        Assert.Equal(["user-0", "anon-0", "anon-1", "anon-2"], await IdsAsync(tab, UserKey));
        Assert.Equal(["anon-0", "anon-1", "anon-2"], await IdsAsync(tab, AnonKey));

        JsonElement completed = await MutateAsync(tab, new { op = "move", key = AnonKey, to = UserKey });

        Assert.True(completed.GetProperty("ok").GetBoolean());
        Assert.Equal(["user-0", "anon-0", "anon-1", "anon-2"], await IdsAsync(tab, UserKey));
        Assert.Empty(await IdsAsync(tab, AnonKey));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Move_AfterACrashIntoOneOwner_NeverMovesTheSameAnswerIntoAnother()
    {
        const string OtherKey = "dit.outbox.v1.u.990200002";
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await tab.EvaluateAsync<int>(RunOps, Appends(AnonKey, "anon-", 2));
        await tab.EvaluateAsync(
            """
            (key) => {
              const setItem = Storage.prototype.setItem;
              const removeItem = Storage.prototype.removeItem;
              let armed = true;
              const fail = (k) => { if (armed && k === key) { armed = false; throw new DOMException('injected', 'QuotaExceededError'); } };
              Storage.prototype.setItem = function (k, v) { fail(k); return setItem.call(this, k, v); };
              Storage.prototype.removeItem = function (k) { fail(k); return removeItem.call(this, k); };
            }
            """,
            AnonKey);
        JsonElement crashed = await MutateAsync(tab, new { op = "move", key = AnonKey, to = UserKey });
        Assert.True(crashed.GetProperty("persisted").GetBoolean());
        await tab.EvaluateAsync<int>(RunOps, Appends(AnonKey, "late-", 1));

        // Another account imports the same browser's answers: only the one no owner holds yet moves.
        JsonElement moved = await MutateAsync(tab, new { op = "move", key = AnonKey, to = OtherKey });

        Assert.True(moved.GetProperty("ok").GetBoolean());
        Assert.Equal(1, moved.GetProperty("count").GetInt32());
        Assert.Equal(["late-0"], await IdsAsync(tab, OtherKey));
        Assert.Equal(["anon-0", "anon-1"], await IdsAsync(tab, UserKey));
        Assert.Empty(await IdsAsync(tab, AnonKey));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Summary_ConcurrentAddAndRemove_MergeById()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        IPage second = await OpenAsync(context);
        static object Add(string id) => new
        {
            op = "summaryAdd",
            key = PendingKey,
            value = new { mockSessionId = id, summary = new { stack = "dotnet", level = "middle", questionCount = 5 } },
        };

        await first.EvaluateAsync<int>(RunOps, Enumerable.Range(0, 100).Select(i => Add("old-" + i)).ToArray());
        object[] removes = [.. Enumerable.Range(0, 100).Select(i => new { op = "summaryRemove", key = PendingKey, ids = new[] { "old-" + i } })];
        object[] adds = [.. Enumerable.Range(0, 100).Select(i => Add("new-" + i)), .. Enumerable.Range(0, 50).Select(i => Add("new-" + i))];

        await Task.WhenAll(first.EvaluateAsync<int>(RunOps, removes), second.EvaluateAsync<int>(RunOps, adds));

        string[] ids = await IdsAsync(first, PendingKey, "items", "mockSessionId");
        Assert.Equal(Enumerable.Range(0, 100).Select(i => "new-" + i).Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task History_ConcurrentAddsFromTwoTabs_KeepBothAndTheNewestTwentyPerStack()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        IPage second = await OpenAsync(context);
        const string HistoryKey = "dit.mock.v1.anon.history";
        static string Id(string prefix, int i) => "01KD" + prefix + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(22 - prefix.Length, '0');
        // Seven fractional digits, as .NET writes them; the minute grows with the index so "newest" is the high indexes.
        static object Add(string id, string stack, int minute) => new
        {
            op = "historyAdd",
            key = HistoryKey,
            value = new { id, stack, level = "middle", questionCount = 5, finishedAt = $"2026-11-12T10:{minute:D2}:00.0000000+00:00", knew = 1, partial = 0, didntKnow = 0, unanswered = 4 },
        };

        object[] fromFirst = [.. Enumerable.Range(0, 15).Select(i => Add(Id("A", i), "dotnet", 2 * i)), Add(Id("J", 0), "java", 1)];
        object[] fromSecond = [.. Enumerable.Range(0, 15).Select(i => Add(Id("B", i), "dotnet", (2 * i) + 1))];

        await Task.WhenAll(first.EvaluateAsync<int>(RunOps, fromFirst), second.EvaluateAsync<int>(RunOps, fromSecond));

        string[] ids = await IdsAsync(first, HistoryKey, "items", "id");
        // 30 dotnet mocks arrived from two tabs at once: exactly the newest 20 stay, and the java mock is not pushed out.
        string[] expected =
        [
            .. Enumerable.Range(5, 10).Select(i => Id("A", i)),
            .. Enumerable.Range(5, 10).Select(i => Id("B", i)),
            Id("J", 0),
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task Ops_OutsideTheirKeyFamily_AreRefusedAndWriteNothing()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        const string Session = "01KC0000000000000000000009";
        await tab.EvaluateAsync("() => { localStorage.setItem('dit.account', 'not json'); localStorage.setItem('dit.outbox.v1.corrupt.1', 'not json'); }");
        string before = await tab.EvaluateAsync<string>(AppEntries);
        object summary = new { mockSessionId = "s1", summary = new { stack = "dotnet" } };
        object document = new { version = 1, sessionId = Session, revision = 1 };
        object[] refused =
        [
            new { op = "append", key = "dit.clock.v1", value = new[] { Entry("a") } },
            new { op = "append", key = PendingKey, value = new[] { Entry("a") } },
            new { op = "append", key = "dit.outbox.v1.u.abc", value = new[] { Entry("a") } },
            new { op = "append", key = "dit.outbox.v1.anon.x", value = new[] { Entry("a") } },
            new { op = "remove", key = PendingKey, ids = new[] { "a" } },
            new { op = "summaryAdd", key = UserKey, value = summary },
            new { op = "summaryAdd", key = "dit.mock.v1.pending.anon", value = summary },
            new { op = "summaryRemove", key = UserKey, ids = new[] { "a" } },
            new { op = "historyAdd", key = "dit.mock.v1.anon.active", value = new { id = "a", stack = "dotnet", finishedAt = "2026-11-12T10:00:00Z" } },
            new { op = "historyAdd", key = "dit.mock.v1.990200001.history", value = new { id = "a", stack = "dotnet", finishedAt = "2026-11-12T10:00:00Z" } },
            new { op = "move", key = AnonKey, to = PendingKey },
            new { op = "move", key = PendingKey, to = AnonKey, shape = "summaries" },
            new { op = "move", key = AnonKey, to = UserKey, shape = "summaries" },
            new { op = "move", key = PendingKey, to = "dit.mock.v1.failed.990200001" },
            new { op = "move", key = AnonKey, to = UserKey, shape = "constructor" },
            new { op = "move", key = AnonKey, to = UserKey, shape = "__proto__" },
            new { op = "quarantine", key = "dit.account" },
            new { op = "quarantine", key = "dit.outbox.v1.corrupt.1" },
            new { op = "quarantine", key = "theme" },
            new { op = "put", key = UserKey, value = document },
            new { op = "put", key = "dit.account", value = document },
            new { op = "put", key = "dit.mock.v1.990200001.not-a-ulid.questions", value = document },
            new { op = "putIfRevision", key = "dit.mock.v1.990200001." + Session + ".questions", value = document, expectedRevision = 0 },
            new { op = "putIfRevision", key = "dit.mock.v1.someone.active", value = document, expectedRevision = 0 },
            new { op = "delete", key = UserKey },
            new { op = "delete", key = "dit.account" },
        ];

        foreach (object op in refused)
        {
            JsonElement result = await MutateAsync(tab, op);
            Assert.True(result.GetProperty("error").GetString() == "bad-op", JsonSerializer.Serialize(op) + " -> " + result.GetRawText());
        }

        Assert.Equal(before, await tab.EvaluateAsync<string>(AppEntries));
        object[] accepted =
        [
            Append(UserKey, "a"),
            Append(AnonKey, "b"),
            new { op = "move", key = AnonKey, to = UserKey },
            new { op = "summaryAdd", key = PendingKey, value = summary },
            new { op = "move", key = PendingKey, to = "dit.mock.v1.failed.990200001", shape = "summaries" },
            new { op = "put", key = "dit.mock.v1.anon." + Session + ".questions", value = document },
            new { op = "putIfRevision", key = "dit.mock.v1.anon.active", value = document, expectedRevision = 0 },
            new { op = "delete", key = "dit.mock.v1.anon.active", sessionId = Session },
        ];
        foreach (object op in accepted)
        {
            JsonElement result = await MutateAsync(tab, op);
            Assert.True(result.GetProperty("ok").GetBoolean(), JsonSerializer.Serialize(op) + " -> " + result.GetRawText());
        }

        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Remove_NeverDropsIdlessEntries()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        string document = JsonSerializer.Serialize(new { version = 1, answers = new object?[] { Entry("a"), null, 5, new { note = "no id" }, Entry("b") } });
        await tab.EvaluateAsync("([key, value]) => localStorage.setItem(key, value)", new[] { UserKey, document });

        // An acknowledgement names ids; it never sweeps entries nobody can name.
        JsonElement acknowledged = await MutateAsync(tab, new { op = "remove", key = UserKey, ids = AckIds });
        Assert.Equal(1, acknowledged.GetProperty("count").GetInt32());
        using (JsonDocument afterAck = JsonDocument.Parse((await tab.EvaluateAsync<string>("key => localStorage.getItem(key)", UserKey))))
        {
            Assert.Equal(4, afterAck.RootElement.GetProperty("answers").GetArrayLength());
        }

        // An unknown member asking for a sweep changes nothing: only named ids ever go.
        JsonElement swept = await MutateAsync(tab, new { op = "remove", key = UserKey, ids = ClearIds, malformed = true });
        Assert.Equal(1, swept.GetProperty("count").GetInt32());
        using (JsonDocument afterSweep = JsonDocument.Parse((await tab.EvaluateAsync<string>("key => localStorage.getItem(key)", UserKey))))
        {
            Assert.Equal(3, afterSweep.RootElement.GetProperty("answers").GetArrayLength());
        }

        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task ClaimAccount_NewerForeignOwnerIsReported_OlderOrFutureOnesAreReplaced()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        const string Claim = "([stored, owner]) => { if (stored === null) { localStorage.removeItem('dit.account'); } else { localStorage.setItem('dit.account', stored); } return window.ditStore.claimAccount(owner); }";
        const string Stored = "() => localStorage.getItem('dit.account')";
        double origin = await tab.EvaluateAsync<double>("() => performance.timeOrigin");
        double now = await tab.EvaluateAsync<double>("() => Date.now()");

        // Published after this page started loading: the page was rendered for an account the cookie no longer holds.
        string? newer = await tab.EvaluateAsync<string?>(Claim, new object[] { $"555|{(long)now}", "anon" });
        Assert.Equal("555", newer);
        Assert.Equal($"555|{(long)now}", await tab.EvaluateAsync<string?>(Stored));

        // Published before this page started: stale, replaced by this page's owner.
        string? older = await tab.EvaluateAsync<string?>(Claim, new object[] { $"555|{(long)origin - 60_000}", "anon" });
        Assert.Null(older);
        Assert.Matches(@"^anon\|[0-9]+$", await tab.EvaluateAsync<string?>(Stored));

        // Dated in the future (the writer's clock was ahead): stale as well, never "newer" than now.
        string? future = await tab.EvaluateAsync<string?>(Claim, new object[] { $"555|{(long)now + 3_600_000}", "anon" });
        Assert.Null(future);
        Assert.Matches(@"^anon\|[0-9]+$", await tab.EvaluateAsync<string?>(Stored));

        // The same owner is never a change.
        Assert.Null(await tab.EvaluateAsync<string?>(Claim, new object[] { $"anon|{(long)now}", "anon" }));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Quarantine_ConcurrentInTwoTabs_KeepsEveryCopy()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        IPage second = await OpenAsync(context);
        await first.EvaluateAsync("() => { for (let i = 1; i <= 200; i++) localStorage.setItem('dit.outbox.v1.u.' + i, 'broken ' + i); }");
        object[] firstOps = [.. Enumerable.Range(1, 100).Select(i => new { op = "quarantine", key = "dit.outbox.v1.u." + i })];
        object[] secondOps = [.. Enumerable.Range(101, 100).Select(i => new { op = "quarantine", key = "dit.outbox.v1.u." + i })];
        double startAt = await first.EvaluateAsync<double>("() => performance.timeOrigin + performance.now() + 300");

        // Both tabs see the same frozen Date.now, so every copy name starts at the same stamp: only the lock on the copy
        // names keeps the two tabs from overwriting each other's copies.
        const string PinClock = "() => { Date.now = () => 1700000000000; }";
        await first.EvaluateAsync(PinClock);
        await second.EvaluateAsync(PinClock);
        await Task.WhenAll(
            first.EvaluateAsync<int>(RunOpsConcurrently, new object[] { firstOps, startAt }),
            second.EvaluateAsync<int>(RunOpsConcurrently, new object[] { secondOps, startAt }));

        string[] copies = await first.EvaluateAsync<string[]>(
            "() => Object.keys(localStorage).filter(k => k.startsWith('dit.outbox.v1.corrupt.')).map(k => localStorage.getItem(k))");
        Assert.Equal(Enumerable.Range(1, 200).Select(i => "broken " + i).Order(StringComparer.Ordinal), copies.Order(StringComparer.Ordinal));
        Assert.Equal(0, await first.EvaluateAsync<int>("() => Object.keys(localStorage).filter(k => k.startsWith('dit.outbox.v1.u.')).length"));
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task PutIfRevision_NonIntegerRevision_IsQuarantinedThenSaved()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        const string Key = "dit.mock.v1.990200001.active";
        await tab.EvaluateAsync("k => localStorage.setItem(k, JSON.stringify({ version: 1, sessionId: 's', revision: 1.5 }))", Key);
        object save = new { op = "putIfRevision", key = Key, value = new { version = 1, sessionId = "s", revision = 1 }, expectedRevision = 0 };

        Assert.Equal("corrupt", (await MutateAsync(tab, save)).GetProperty("error").GetString());
        Assert.Equal(1, (await MutateAsync(tab, new { op = "quarantine", key = Key })).GetProperty("count").GetInt32());
        Assert.True((await MutateAsync(tab, save)).GetProperty("ok").GetBoolean());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task PutIfRevision_OtherSessionAtMatchingRevision_IsARevisionConflict()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        const string Key = "dit.mock.v1.990200001.active";
        object first = new { op = "putIfRevision", key = Key, value = new { version = 1, sessionId = "a", revision = 1 }, expectedRevision = 0, sessionId = "a" };
        object stale = new { op = "putIfRevision", key = Key, value = new { version = 1, sessionId = "b", revision = 2 }, expectedRevision = 1, sessionId = "b" };
        object next = new { op = "putIfRevision", key = Key, value = new { version = 1, sessionId = "a", revision = 2 }, expectedRevision = 1, sessionId = "a" };

        Assert.True((await MutateAsync(tab, first)).GetProperty("ok").GetBoolean());
        JsonElement refused = await MutateAsync(tab, stale);
        Assert.Equal("revision", refused.GetProperty("error").GetString());
        Assert.Equal(1, refused.GetProperty("revision").GetInt64());
        Assert.Contains("\"sessionId\":\"a\"", await tab.EvaluateAsync<string>("k => localStorage.getItem(k)", Key), StringComparison.Ordinal);
        Assert.True((await MutateAsync(tab, next)).GetProperty("ok").GetBoolean());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Documents_DeeperThan64_AreCorruptAndNeverWritten()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        const string Nested = "([k, n]) => localStorage.setItem(k, '{\"version\":1,\"answers\":[],\"x\":' + '['.repeat(n) + ']'.repeat(n) + '}')";

        // The object plus 63 nested arrays is 64 levels, the C# JsonDocument limit; one more level is corrupt in both.
        await tab.EvaluateAsync(Nested, new object[] { UserKey, 63 });
        Assert.True((await MutateAsync(tab, Append(UserKey, "a"))).GetProperty("ok").GetBoolean());
        await tab.EvaluateAsync(Nested, new object[] { AnonKey, 64 });
        Assert.Equal("corrupt", (await MutateAsync(tab, Append(AnonKey, "a"))).GetProperty("error").GetString());
        Assert.Equal(1, (await MutateAsync(tab, new { op = "quarantine", key = AnonKey })).GetProperty("count").GetInt32());

        string deep = await tab.EvaluateAsync<string>(
            """
            async () => {
              let nested = []; for (let i = 0; i < 61; i++) nested = [nested];
              return await window.ditStore.mutate(JSON.stringify({ op: 'append', key: 'dit.outbox.v1.u.990200002', value: [{ clientAnswerId: 'd', x: nested }] }));
            }
            """);
        Assert.Contains("\"bad-op\"", deep, StringComparison.Ordinal);
        Assert.Null(await tab.EvaluateAsync<string?>("() => localStorage.getItem('dit.outbox.v1.u.990200002')"));
        host.AssertNoCspViolations(tab);
    }
}
