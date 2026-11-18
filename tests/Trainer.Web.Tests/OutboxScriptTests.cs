using System.Text.Json;

namespace Trainer.Web.Tests;

/// <summary>
/// The parts of <c>outbox.js</c> the static edition's progress checkpoint stands on, run under Node against an in-memory
/// <c>localStorage</c> and <c>navigator.locks</c>: the fingerprint of a log, what an append reports, and which keys a
/// whole-value write may touch. The lock and cross-tab behaviour is the browser tests' (<c>OutboxConcurrencyTests</c>).
/// </summary>
public sealed class OutboxScriptTests
{
    private const string Harness = """
        const fs = require('fs');
        const vm = require('vm');
        const source = fs.readFileSync(process.argv[2], 'utf8');
        const steps = JSON.parse(process.argv[3]);

        function load() {
          const storage = new Map();
          const local = {
            getItem: (k) => (storage.has(k) ? storage.get(k) : null),
            setItem: (k, v) => { storage.set(k, String(v)); },
            removeItem: (k) => { storage.delete(k); },
            key: (i) => Array.from(storage.keys())[i] ?? null,
            get length() { return storage.size; },
          };
          const window = { localStorage: local, addEventListener() {}, removeEventListener() {} };
          const navigator = { locks: { request: (name, callback) => Promise.resolve().then(callback) } };
          const context = vm.createContext({ window, navigator, console, performance: { timeOrigin: 0 }, Date, Map, Set, Math, JSON, Object, Array, Number, String, Promise, Error });
          new vm.Script(source).runInContext(context);
          return { store: window.ditStore, storage };
        }

        const entry = (id, grade, at) => ({ clientAnswerId: id, questionId: 'q', grade, usedHint: false, mode: 'practice', mockSessionId: null, answeredAt: at });

        (async () => {
          const { store, storage } = load();
          const out = [];
          for (const step of steps) {
            if (step.set !== undefined) { storage.set(step.key, step.set); out.push(null); continue; }
            if (step.fingerprint) { out.push(store.fingerprint(step.fingerprint)); continue; }
            if (step.raw) { out.push(storage.has(step.raw) ? storage.get(step.raw) : null); continue; }
            if (step.opJson) { out.push(JSON.parse(await store.mutate(step.opJson))); continue; }
            if (step.append) {
              const entries = step.append.ids.map((id) => entry(id, step.append.grade || 'knew', step.append.at || '2026-11-12T10:00:00Z'));
              out.push(JSON.parse(await store.mutate(JSON.stringify({ op: 'append', key: step.key || 'dit.outbox.v1.anon', value: entries }))));
              continue;
            }
            out.push(JSON.parse(await store.mutate(JSON.stringify(step.op))));
          }
          process.stdout.write(JSON.stringify(out));
        })();
        """;

    private const string Key = "dit.outbox.v1.anon";

    private static async Task<JsonElement[]> RunAsync(params object[] steps)
    {
        using JsonDocument result = await NodeHarness.RunAsync(
            Harness,
            TestContext.Current.CancellationToken,
            NodeHarness.WwwRootFile("js", "outbox.js"),
            JsonSerializer.Serialize(steps));
        return [.. result.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static object Id(int n) => "01JD" + n.ToString("D22", System.Globalization.CultureInfo.InvariantCulture);

    private static object Append(params int[] ids) => new { append = new { ids = ids.Select(i => Id(i)).ToArray() } };

    [Fact]
    public async Task Fingerprint_TellsTheStateOfALogFromItsCountAndTail()
    {
        JsonElement[] r = await RunAsync(
            new { fingerprint = Key },
            Append(1, 2),
            new { fingerprint = Key },
            Append(3),
            new { fingerprint = Key });

        string empty = r[0].GetString()!;
        string two = r[2].GetString()!;
        string three = r[4].GetString()!;
        Assert.StartsWith("0:", empty, StringComparison.Ordinal);
        Assert.StartsWith("2:", two, StringComparison.Ordinal);
        Assert.StartsWith("3:", three, StringComparison.Ordinal);
        Assert.Equal(3, new[] { empty, two, three }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Fingerprint_IsTheSameForTheSameLogAndSeesAChangedTail()
    {
        JsonElement[] same = await RunAsync(Append(1, 2, 3), new { fingerprint = Key }, new { raw = Key });
        string log = same[2].GetString()!;
        JsonElement[] again = await RunAsync(new { set = log, key = Key }, new { fingerprint = Key });
        JsonElement[] changed = await RunAsync(
            new { set = log.Replace("knew", "partial", StringComparison.Ordinal), key = Key },
            new { fingerprint = Key });

        Assert.Equal(same[1].GetString(), again[1].GetString());
        // Same count, other grades in the tail: not the same log state.
        Assert.NotEqual(same[1].GetString(), changed[1].GetString());
        Assert.StartsWith("3:", changed[1].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fingerprint_HashesOnlyTheTail_SoAnOldEntryDoesNotChangeIt()
    {
        // Sixty-six entries: editing the first two (outside the last sixty-four) is not noticed, by design.
        int[] ids = [.. Enumerable.Range(1, 66)];
        JsonElement[] r = await RunAsync(Append(ids), new { fingerprint = Key }, new { raw = Key });
        string log = r[2].GetString()!;
        JsonElement[] edited = await RunAsync(
            new { set = ReplaceFirst(log, "knew", "partial"), key = Key },
            new { fingerprint = Key });

        Assert.Equal(r[1].GetString(), edited[1].GetString());
    }

    private static string ReplaceFirst(string text, string from, string to)
    {
        int at = text.IndexOf(from, StringComparison.Ordinal);
        return text[..at] + to + text[(at + from.Length)..];
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"answers\":[]}")]
    [InlineData("{\"version\":1,\"answers\":7}")]
    public async Task Fingerprint_IsNullForAnythingThatIsNotAReadableOutbox(string raw)
    {
        JsonElement[] r = await RunAsync(new { set = raw, key = Key }, new { fingerprint = Key });

        Assert.Equal(JsonValueKind.Null, r[1].ValueKind);
    }

    [Theory]
    [InlineData("dit.outbox.v1.corrupt.1")]
    [InlineData("dit.clock.v1")]
    [InlineData("anything")]
    public async Task Fingerprint_IsOnlyForOutboxKeys(string key)
    {
        JsonElement[] r = await RunAsync(new { fingerprint = key });

        Assert.Equal(JsonValueKind.Null, r[0].ValueKind);
    }

    [Fact]
    public async Task Append_ReportsTheLogStateBeforeAndAfterAndHowManyEntriesWereNew()
    {
        JsonElement[] r = await RunAsync(
            new { fingerprint = Key },
            Append(1, 2),
            Append(2, 3),
            new { fingerprint = Key });

        JsonElement first = r[1];
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal(2, first.GetProperty("count").GetInt32());
        Assert.Equal(2, first.GetProperty("added").GetInt32());
        Assert.Equal(r[0].GetString(), first.GetProperty("previous").GetString());

        JsonElement second = r[2];
        // Entry 2 was already there and is replaced in place; only entry 3 is new.
        Assert.Equal(2, second.GetProperty("count").GetInt32());
        Assert.Equal(1, second.GetProperty("added").GetInt32());
        Assert.Equal(first.GetProperty("fingerprint").GetString(), second.GetProperty("previous").GetString());
        Assert.Equal(r[3].GetString(), second.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task Append_ToAnotherShape_ReportsNoFingerprint()
    {
        JsonElement[] r = await RunAsync(
            new { op = new { op = "summaryAdd", key = "dit.mock.v1.pending.42", value = new { mockSessionId = "01JD0000000000000000000001" } } });

        Assert.True(r[0].GetProperty("ok").GetBoolean());
        Assert.False(r[0].TryGetProperty("fingerprint", out _));
    }

    [Fact]
    public async Task Put_IsAllowedForTheCheckpointKeyOnly()
    {
        JsonElement[] r = await RunAsync(
            new { op = new { op = "put", key = "dit.progress.v1.anon", value = new { version = 1, fingerprint = "0:0" } } },
            new { op = new { op = "put", key = "dit.progress.v1.other", value = new { version = 1 } } },
            new { op = new { op = "put", key = "dit.outbox.v1.anon", value = new { version = 1, answers = Array.Empty<object>() } } },
            new { raw = "dit.progress.v1.anon" });

        Assert.True(r[0].GetProperty("ok").GetBoolean());
        Assert.Equal("bad-op", r[1].GetProperty("error").GetString());
        Assert.Equal("bad-op", r[2].GetProperty("error").GetString());
        Assert.Contains("\"fingerprint\":\"0:0\"", r[3].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_IsAllowedForTheCheckpointKey_AndRemovesIt()
    {
        JsonElement[] r = await RunAsync(
            new { op = new { op = "put", key = "dit.progress.v1.anon", value = new { version = 1, fingerprint = "0:0" } } },
            new { op = new { op = "delete", key = "dit.progress.v1.anon" } },
            new { op = new { op = "delete", key = "dit.progress.v1.anon" } },
            new { op = new { op = "delete", key = "dit.outbox.v1.anon" } },
            new { raw = "dit.progress.v1.anon" });

        Assert.True(r[1].GetProperty("ok").GetBoolean());
        Assert.Equal(1, r[1].GetProperty("count").GetInt32());
        Assert.Equal(0, r[2].GetProperty("count").GetInt32());
        Assert.Equal("bad-op", r[3].GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, r[4].ValueKind);
    }

    [Fact]
    public async Task Append_StillRefusesAnEntryNestedTooDeeply_AndAStoredDocumentThatIs()
    {
        string deep = string.Concat(Enumerable.Repeat("[", 70)) + string.Concat(Enumerable.Repeat("]", 70));
        JsonElement[] r = await RunAsync(
            new { opJson = "{\"op\":\"append\",\"key\":\"" + Key + "\",\"value\":[{\"clientAnswerId\":\"" + Id(1) + "\",\"nested\":" + deep + "}]}" },
            new { raw = Key },
            new { set = "{\"version\":1,\"answers\":[{\"clientAnswerId\":\"x\",\"n\":" + deep + "}]}", key = Key },
            Append(2));

        // The entry is refused and nothing is written; a stored document nested past the limit is corrupt, as before.
        Assert.Equal("bad-op", r[0].GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, r[1].ValueKind);
        Assert.Equal("corrupt", r[3].GetProperty("error").GetString());
    }
}
