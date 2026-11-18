using System.Text.Json;

namespace Trainer.Web.Tests;

/// <summary>
/// The static edition's <c>loadBootResource</c> (<c>islands-start.js</c>, ADR 0027) runs under Node against a fake
/// <c>fetch</c> and <c>Blazor</c>, with the real vendored decoder and the real <c>crypto.subtle</c>: a wrong answer from
/// either would otherwise pass unnoticed. The browser path is checked by the live checks of Task 17.
/// </summary>
public sealed class BrotliBootLoaderTests
{
    private const string DefaultUri = "https://example.test/dit/_framework/Trainer.Domain.abcdefghij.wasm";
    private const string DefaultName = "Trainer.Domain.abcdefghij.wasm";

    private const string Harness = """
        const fs = require('fs');
        const vm = require('vm');
        const zlib = require('zlib');
        const nodeCrypto = require('crypto');
        const path = require('path');
        const os = require('os');
        const scriptPath = process.argv[2];
        const source = fs.readFileSync(scriptPath, 'utf8');
        const plain = Buffer.from('boot resource bytes '.repeat(500));
        const sha = (bytes) => 'sha256-' + nodeCrypto.createHash('sha256').update(bytes).digest('base64');
        const br = (bytes) => zlib.brotliCompressSync(bytes);

        async function run(scenario) {
          const fetches = [];
          const tableFetches = [];
          const fetchImpl = async (url, init) => {
            if (String(url).startsWith('file:')) {
              // The table file, fetched by the retry. A fetch whose integrity does not match the bytes fails as in a browser.
              tableFetches.push({ url: String(url), init: init === undefined ? null : init });
              const bytes = fs.readFileSync(require('url').fileURLToPath(String(url)));
              if (init && init.integrity) {
                const [algorithm, expected] = init.integrity.split('-');
                if (nodeCrypto.createHash(algorithm).update(bytes).digest('base64') !== expected) throw new TypeError('integrity');
              }
              return new Response(bytes, { status: 200 });
            }
            fetches.push({ url: String(url), init: init === undefined ? null : init });
            if (String(url).endsWith('.br')) {
              switch (scenario.brotli) {
                case 'ok': return new Response(br(plain), { status: 200 });
                case 'missing': return new Response('not found', { status: 404 });
                case 'corrupt': return new Response(Buffer.from('this is not a brotli stream, only text'), { status: 200 });
                case 'otherBytes': return new Response(br(Buffer.from('some other file entirely')), { status: 200 });
                case 'network': throw new TypeError('network');
              }
            }
            if (scenario.fallback === 'fail') throw new TypeError('network');
            // A fetch with an integrity that is answered by anything but the promised bytes (an error page, or a file that
            // changed) is rejected by the browser as a TypeError, whatever the status was.
            if (scenario.fallback === 'gone' || scenario.fallback === 'mismatch' || scenario.fallback === 'headUnsupported') {
              if (init && init.integrity) throw new TypeError('integrity');
              if (scenario.fallback === 'headUnsupported') return new Response('', { status: 405 });
              return scenario.fallback === 'gone' ? new Response('not found', { status: 404 }) : new Response(plain, { status: 200, headers: { 'x-source': 'plain' } });
            }
            return new Response(plain, { status: 200, headers: { 'x-source': 'plain' } });
          };
          let started = null;
          const failures = [];
          const warnings = [];
          const documentElement = { lang: 'en', dataset: { edition: scenario.edition === undefined ? 'static' : scenario.edition } };
          const sandbox = {
            console: { error(message) { failures.push(String(message)); }, warn(message) { if (String(message).startsWith('Boot integrity')) warnings.push(1); }, log() {} },
            Promise, Object, String, Int8Array, Uint8Array, Response, URL, btoa, setTimeout,
            fetch: fetchImpl,
            crypto: scenario.noSubtle ? {} : nodeCrypto.webcrypto,
            Blazor: { start(options) { started = options; return new Promise(() => {}); }, addEventListener() {} },
            performance: { now: () => 0 },
            location: { search: '' },
            matchMedia: () => ({ matches: false }),
            MutationObserver: class { observe() {} disconnect() {} },
            document: {
              documentElement,
              baseURI: 'https://example.test/dit/',
              currentScript: scenario.flag === false ? { dataset: {} } : { dataset: { brotli: 'true' } },
              querySelectorAll: () => [],
              querySelector: () => null,
            },
          };
          sandbox.window = sandbox;
          sandbox.addEventListener = () => {};
          const context = vm.createContext(sandbox);
          // A directory per scenario, laid out like wwwroot/js: the loader imports ./vendor/brotli-decode.mjs and
          // ./boot-integrity.mjs relative to itself, and the module loader caches by URL.
          const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dit-loader-'));
          fs.mkdirSync(path.join(dir, 'vendor'));
          fs.copyFileSync(path.join(path.dirname(scriptPath), 'vendor', 'brotli-decode.mjs'), path.join(dir, 'vendor', 'brotli-decode.mjs'));
          const writeTable = () => {
            const entries = scenario.table === 'noEntry' ? {} : { [scenario.tableKey || scenario.name]: scenario.tableIntegrity == null ? sha(plain) : scenario.tableIntegrity };
            // Through an import map the table lives under its fingerprinted name and the unfingerprinted alias never exists.
            const tableFile = scenario.table === 'lateViaImportMap' ? 'boot-integrity.abcdefghij.mjs' : 'boot-integrity.mjs';
            fs.writeFileSync(path.join(dir, tableFile), scenario.tableText == null ? 'export default ' + JSON.stringify(entries) + ';\n' : scenario.tableText);
          };
          if (scenario.table !== 'missingFile' && !String(scenario.table).startsWith('late')) writeTable();
          // 'late': the file shows up while the loader's first import has failed and its retry is still waiting.
          if (scenario.table === 'late' || scenario.table === 'lateViaImportMap') setTimeout(writeTable, 100);
          if (scenario.table === 'lateViaImportMap') {
            // The page's import map names the fingerprinted file; Node has no import maps, so the loader's first import of the
            // alias fails and its retry has to find the file through the map in the page, as the browser's retry does.
            const scriptUrl = require('url').pathToFileURL(path.join(dir, 'islands-start.js')).href;
            const alias = new URL('./boot-integrity.mjs', scriptUrl).href;
            sandbox.document.baseURI = require('url').pathToFileURL(dir).href + '/';
            sandbox.document.currentScript = { dataset: { brotli: 'true' }, src: scriptUrl };
            // The import map's integrity is the hash of the file as the export wrote it, or a wrong one, or absent.
            const tableUrl = new URL('boot-integrity.abcdefghij.mjs', sandbox.document.baseURI).href;
            const mapIntegrity = () => {
              if (scenario.mapIntegrity === 'none') return {};
              const real = fs.readFileSync(path.join(dir, 'boot-integrity.abcdefghij.mjs'));
              const hashed = scenario.mapIntegrity === 'wrong' ? Buffer.concat([real, Buffer.from(' ')]) : real;
              return { integrity: { [tableUrl]: 'sha384-' + nodeCrypto.createHash('sha384').update(hashed).digest('base64') } };
            };
            sandbox.document.querySelector = (selector) => (selector === 'script[type="importmap"]'
              ? { textContent: JSON.stringify({ imports: { [alias]: 'boot-integrity.abcdefghij.mjs' }, ...mapIntegrity() }) }
              : null);
          }
          const filename = scenario.decoderMissing ? path.join(os.tmpdir(), 'dit-no-decoder', 'islands-start.js') : path.join(dir, 'islands-start.js');
          try {
            new vm.Script(source, { filename, importModuleDynamically: vm.constants.USE_MAIN_CONTEXT_DEFAULT_LOADER }).runInContext(context);
            return await exercise(scenario, started, fetches, failures, writeTable, tableFetches, warnings);
          } finally {
            fs.rmSync(dir, { recursive: true, force: true });
          }
        }

        async function exercise(scenario, started, fetches, failures, writeTable, tableFetches, warnings) {
          const options = started.webAssembly;
          if (scenario.checkOnlyOptions) {
            return { hasLoader: typeof options.loadBootResource === 'function', culture: options.applicationCulture };
          }
          // The runtime hands the loader an empty integrity for every fingerprinted resource (.NET 10 reads a hash field the
          // boot config does not have), so the table is the source; a value from the runtime is only a fallback.
          const result = await load(options, scenario);
          if (result.returnedUndefined) return result;
          if (scenario.table === 'lateForSecondResource') {
            // The first resource found no table (and has been loaded plain); the next resource asks again.
            writeTable();
            result.second = await load(options, scenario);
          }
          if (scenario.twice) result.second = await load(options, scenario);
          result.fetches = fetches;
          result.tableFetches = tableFetches;
          result.warnings = warnings.length;
          result.bootFailureShown = failures.includes('Blazor failed to start');
          return result;
        }

        async function load(options, scenario) {
          const returned = options.loadBootResource(scenario.type, scenario.name, scenario.uri, scenario.runtimeIntegrity || '');
          const result = { returnedUndefined: returned === undefined, returnedPromise: returned instanceof Promise };
          if (returned === undefined) return result;
          try {
            const response = await returned;
            result.outcome = response === undefined ? 'undefined' : 'resolved';
            result.status = response.status;
            result.source = response.headers.get('x-source');
            result.contentType = response.headers.get('content-type');
            result.bodyMatches = Buffer.from(await response.arrayBuffer()).equals(plain);
          } catch (error) {
            result.outcome = 'rejected';
          }
          return result;
        }

        (async () => {
          const scenarios = JSON.parse(process.argv[3]);
          const results = [];
          for (const scenario of scenarios) results.push(await run(scenario));
          process.stdout.write(JSON.stringify(results));
        })();
        """;

    private static async Task<JsonElement[]> RunAsync(params object[] scenarios)
    {
        using JsonDocument result = await NodeHarness.RunAsync(
            Harness,
            TestContext.Current.CancellationToken,
            NodeHarness.WwwRootFile("js", "islands-start.js"),
            JsonSerializer.Serialize(scenarios));
        return [.. result.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    // tableIntegrity null is the correct hash of the file; table is "ok", "noEntry" (the resource is not listed) or
    // "missingFile" (boot-integrity.mjs does not load). runtimeIntegrity is what the runtime passes, which it leaves empty.
    private static object Scenario(
        string brotli,
        string type = "dotnetwasm",
        string fallback = "ok",
        string? tableIntegrity = null,
        string table = "ok",
        string? runtimeIntegrity = null,
        bool decoderMissing = false,
        bool noSubtle = false,
        string uri = DefaultUri,
        string name = DefaultName,
        string? tableKey = null,
        string mapIntegrity = "good",
        string? tableText = null,
        bool twice = false) =>
        new { brotli, type, fallback, tableIntegrity, table, runtimeIntegrity, decoderMissing, noSubtle, uri, name, tableKey, mapIntegrity, tableText, twice };

    private static string[] FetchedUrls(JsonElement run) =>
        [.. run.GetProperty("fetches").EnumerateArray().Select(f => f.GetProperty("url").GetString()!)];

    [Fact]
    public async Task Loader_DecodesTheBrFileAndAnswersWithTheOriginalTypeAndBytes()
    {
        JsonElement[] runs = await RunAsync(Scenario("ok"), Scenario("ok", type: "assembly"));

        JsonElement wasm = runs[0];
        Assert.Equal("resolved", wasm.GetProperty("outcome").GetString());
        Assert.Equal("application/wasm", wasm.GetProperty("contentType").GetString());
        Assert.True(wasm.GetProperty("bodyMatches").GetBoolean(), "the response must carry the decoded bytes");
        Assert.Equal([DefaultUri + ".br"], FetchedUrls(wasm));
        // The default cache mode on purpose: the URL is content-addressed, and no-cache would revalidate on every load.
        Assert.Equal(JsonValueKind.Null, wasm.GetProperty("fetches")[0].GetProperty("init").ValueKind);
        Assert.Equal("application/octet-stream", runs[1].GetProperty("contentType").GetString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("network")]
    [InlineData("otherBytes")]
    public async Task Loader_FallsBackToTheUncompressedFileWithIntegrity_WhenTheBrFileIsUnusable(string brotli)
    {
        JsonElement[] runs = await RunAsync(Scenario(brotli));

        JsonElement run = runs[0];
        Assert.Equal("resolved", run.GetProperty("outcome").GetString());
        Assert.Equal("plain", run.GetProperty("source").GetString());
        Assert.True(run.GetProperty("bodyMatches").GetBoolean());
        Assert.Equal([DefaultUri + ".br", DefaultUri], FetchedUrls(run));
        JsonElement init = run.GetProperty("fetches")[1].GetProperty("init");
        Assert.StartsWith("sha256-", init.GetProperty("integrity").GetString(), StringComparison.Ordinal);
        Assert.False(init.TryGetProperty("cache", out _), "the fallback uses the default cache mode too");
    }

    [Fact]
    public async Task Loader_FallsBack_WhenTheDecodedBytesDoNotMatchTheIntegrity()
    {
        // A valid .br stream that decodes to other bytes than the manifest promised is what a tampered or stale file
        // looks like; it must never reach the runtime.
        string wrong = "sha256-" + Convert.ToBase64String(new byte[32]);
        JsonElement[] runs = await RunAsync(Scenario("ok", tableIntegrity: wrong));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
        Assert.Equal([DefaultUri + ".br", DefaultUri], FetchedUrls(runs[0]));
        Assert.Equal(wrong, runs[0].GetProperty("fetches")[1].GetProperty("init").GetProperty("integrity").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha384-AAAA")]
    public async Task Loader_FallsBack_WhenThereIsNoSha256IntegrityToVerifyAgainst(string integrity)
    {
        JsonElement[] runs = await RunAsync(Scenario("ok", tableIntegrity: integrity));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_VerifiesAgainstTheBootIntegrityTable_WhenTheRuntimePassesNoIntegrity()
    {
        // .NET 10 passes an empty integrity for every fingerprinted boot resource; the export's table is the source.
        JsonElement[] runs = await RunAsync(Scenario("ok", runtimeIntegrity: ""));

        Assert.Null(runs[0].GetProperty("source").GetString());
        Assert.True(runs[0].GetProperty("bodyMatches").GetBoolean());
        Assert.Equal([DefaultUri + ".br"], FetchedUrls(runs[0]));
    }

    [Fact]
    public async Task Loader_FallbackFetchCarriesTheTablesIntegrity_NotTheEmptyOneOfTheRuntime()
    {
        JsonElement[] runs = await RunAsync(Scenario("missing", runtimeIntegrity: ""));

        JsonElement init = runs[0].GetProperty("fetches")[1].GetProperty("init");
        Assert.StartsWith("sha256-", init.GetProperty("integrity").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("noEntry")]
    [InlineData("missingFile")]
    public async Task Loader_RefusesTheDecodedBytes_AndLoadsThePlainFileWithTheRuntimesDefaultTrust_WhenTheTableCannotNameTheResource(string table)
    {
        // Decoded bytes are never used without a verified entry. The plain file that replaces them is fetched with the
        // integrity the runtime itself passed (empty), which is the trust the runtime gives a file it loads on its own.
        JsonElement[] runs = await RunAsync(Scenario("ok", table: table, runtimeIntegrity: ""));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
        Assert.Equal([DefaultUri + ".br", DefaultUri], FetchedUrls(runs[0]));
        Assert.Equal(string.Empty, runs[0].GetProperty("fetches")[1].GetProperty("init").GetProperty("integrity").GetString());
    }

    [Fact]
    public async Task Loader_KeysTheTableByThePathUnderFramework_SoASatelliteAssemblyIsFound()
    {
        // The runtime names a satellite assembly by its file name alone, but the file lives in a culture folder; the table
        // is keyed by the path the loader fetches, which is the one thing both sides know.
        const string Satellite = "https://example.test/dit/_framework/ru/Trainer.Web.Client.resources.abcdefghij.wasm";
        JsonElement[] runs = await RunAsync(Scenario("ok", type: "assembly", uri: Satellite, name: "Trainer.Web.Client.resources.abcdefghij.wasm", tableKey: "ru/Trainer.Web.Client.resources.abcdefghij.wasm"));

        Assert.Null(runs[0].GetProperty("source").GetString());
        Assert.Equal([Satellite + ".br"], FetchedUrls(runs[0]));
    }

    [Fact]
    public async Task Loader_PrefersTheTableOverAValueFromTheRuntime()
    {
        // A runtime that does pass an integrity is not trusted over the file the export wrote and verified.
        string other = "sha256-" + Convert.ToBase64String(new byte[32]);
        JsonElement[] runs = await RunAsync(Scenario("ok", runtimeIntegrity: other));

        Assert.Null(runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_UsesTheRuntimesIntegrity_WhenTheTableHasNoEntry()
    {
        JsonElement[] runs = await RunAsync(Scenario("ok", table: "noEntry", runtimeIntegrity: "sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("boot resource bytes ", 500)))))));

        Assert.Null(runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_FallsBack_WhenTheDecoderModuleCannotBeLoaded()
    {
        JsonElement[] runs = await RunAsync(Scenario("ok", decoderMissing: true));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_FallsBack_WhenCryptoSubtleIsMissing()
    {
        // Outside a secure context crypto.subtle is undefined; the uncompressed file is verified by the browser instead.
        JsonElement[] runs = await RunAsync(Scenario("ok", noSubtle: true));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_AMissingUncompressedFileAnswersWithItsHttpStatus_NotAsAnIntegrityFailure()
    {
        // The browser reports a 404 for a fetch with an integrity as a TypeError. The runtime treats a rejection and an HTTP
        // error differently, and the page's recovery (islands-start.js) depends on the runtime's own error for a missing file,
        // so the loader asks again without integrity to tell the two apart.
        JsonElement[] runs = await RunAsync(Scenario("missing", fallback: "gone"));

        Assert.Equal("resolved", runs[0].GetProperty("outcome").GetString());
        Assert.Equal(404, runs[0].GetProperty("status").GetInt32());
        Assert.Equal([DefaultUri + ".br", DefaultUri, DefaultUri], FetchedUrls(runs[0]));
        Assert.StartsWith("sha256-", runs[0].GetProperty("fetches")[1].GetProperty("init").GetProperty("integrity").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("mismatch")]
    public async Task Loader_TheStatusProbeIsAHeadRequest_SoTheServiceWorkerNeverStoresItsBytes(string fallback)
    {
        // The probe has no integrity to check and exists for its status only; the worker stores any good GET it sees under
        // the file's content-addressed name, and a HEAD is never intercepted and carries no body.
        JsonElement[] runs = await RunAsync(Scenario("missing", fallback: fallback));

        JsonElement probe = runs[0].GetProperty("fetches")[2];
        Assert.Equal(DefaultUri, probe.GetProperty("url").GetString());
        Assert.Equal("HEAD", probe.GetProperty("init").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Loader_AHostThatRefusesHead_LeavesTheOriginalFailureStanding()
    {
        // A 405 or 501 answers the method, not the file: it must not be handed to the runtime as the file's status.
        JsonElement[] runs = await RunAsync(Scenario("missing", fallback: "headUnsupported"));

        Assert.Equal("rejected", runs[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Loader_TheRetryNeedsTheImportMap_AndLoadsUncompressedWithoutIt()
    {
        // The retry never imports (that would run an unverified table), so a page without an import map entry for the table
        // has nothing to retry through and the first file is loaded plain.
        JsonElement[] runs = await RunAsync(Scenario("ok", table: "late"));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_TheRetryFindsTheTableThroughThePagesImportMap_BecauseAQueryHasNoEntryOfItsOwn()
    {
        // A browser keeps a failed module fetch, so the retry asks for the file under a query; the URL is the fingerprinted
        // route the page's import map gives the specifier, read from the map, not the alias, which does not exist on the host.
        JsonElement[] runs = await RunAsync(Scenario("ok", table: "lateViaImportMap"));

        Assert.Equal("resolved", runs[0].GetProperty("outcome").GetString());
        Assert.Null(runs[0].GetProperty("source").GetString());
        Assert.True(runs[0].GetProperty("bodyMatches").GetBoolean());
        Assert.Equal([DefaultUri + ".br"], FetchedUrls(runs[0]));
        JsonElement tableFetch = Assert.Single(runs[0].GetProperty("tableFetches").EnumerateArray());
        Assert.EndsWith("/boot-integrity.abcdefghij.mjs", tableFetch.GetProperty("url").GetString());
        Assert.StartsWith("sha384-", tableFetch.GetProperty("init").GetProperty("integrity").GetString());
        Assert.Equal("no-store", tableFetch.GetProperty("init").GetProperty("cache").GetString());
    }

    [Theory]
    [InlineData("wrong", null)]
    [InlineData("none", null)]
    [InlineData("good", "export default [\"sha256-x\"];\n")]
    [InlineData("good", "export default {\"a\":1};\n")]
    [InlineData("good", "globalThis.pwned = 1; export default {};\n")]
    public async Task Loader_TheRetryKeepsTheEmptyTable_WhenTheBytesAreNotTheVerifiedTable(string mapIntegrity, string? tableText)
    {
        // The table is the root of trust for the decoded files: bytes that fail the map's integrity (an altered table), a
        // map without integrity for it, or text that is not the exporter's format are never used, and nothing is executed.
        JsonElement[] runs = await RunAsync(Scenario("ok", table: "lateViaImportMap", mapIntegrity: mapIntegrity, tableText: tableText));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Loader_WarnsOncePerPage_WhenTheTableCannotBeLoaded()
    {
        JsonElement[] runs = await RunAsync(Scenario("ok", table: "missingFile", twice: true));

        Assert.Equal("plain", runs[0].GetProperty("second").GetProperty("source").GetString());
        Assert.Equal(1, runs[0].GetProperty("warnings").GetInt32());
    }

    [Fact]
    public async Task Loader_DoesNotRememberAFailedTableImport_ForTheNextResource()
    {
        // Both attempts for the first file fail, so it is loaded uncompressed; the table is there for the next file, which
        // must not inherit the failure for the page's lifetime.
        JsonElement[] runs = await RunAsync(Scenario("ok", table: "lateForSecondResource"));

        Assert.Equal("plain", runs[0].GetProperty("source").GetString());
        JsonElement second = runs[0].GetProperty("second");
        Assert.Equal("resolved", second.GetProperty("outcome").GetString());
        Assert.Null(second.GetProperty("source").GetString());
        Assert.True(second.GetProperty("bodyMatches").GetBoolean());
    }

    [Theory]
    [InlineData("dotnetwasm", "gone", true)]
    [InlineData("assembly", "gone", true)]
    [InlineData("globalization", "gone", true)]
    [InlineData("assembly", "fail", true)]
    [InlineData("assembly", "mismatch", true)]
    [InlineData("pdb", "gone", false)]
    [InlineData("assembly", "ok", false)]
    public async Task Loader_ShowsTheRecoveryItself_WhenARuntimeFileCannotBeLoadedAtAll(string type, string fallback, bool shown)
    {
        // The runtime's own "Failed to start platform" can come late or, for an HTTP error, not at all, so the loader reports a
        // runtime file that is gone (or refused) the moment it knows. An optional file (pdb) is not a failed start.
        JsonElement[] runs = await RunAsync(Scenario("missing", type: type, fallback: fallback));

        Assert.Equal(shown, runs[0].GetProperty("bootFailureShown").GetBoolean());
    }

    [Fact]
    public async Task Loader_StillRejects_WhenTheUncompressedFileIsThereButFailsItsIntegrity()
    {
        // The second fetch is only a probe for a status: a good answer to it never reaches the runtime unverified.
        JsonElement[] runs = await RunAsync(Scenario("missing", fallback: "mismatch"));

        Assert.Equal("rejected", runs[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Loader_NeverResolvesToUndefined_WhenTheFallbackFailsToo()
    {
        JsonElement[] runs = await RunAsync(Scenario("missing", fallback: "fail"));

        Assert.True(runs[0].GetProperty("returnedPromise").GetBoolean());
        Assert.Equal("rejected", runs[0].GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("dotnetjs")]
    [InlineData("configuration")]
    [InlineData("manifest")]
    [InlineData("symbols")]
    [InlineData("hasOwnProperty")]
    public async Task Loader_LeavesOtherTypesToTheRuntimeSynchronously(string type)
    {
        JsonElement[] runs = await RunAsync(Scenario("ok", type: type));

        Assert.True(runs[0].GetProperty("returnedUndefined").GetBoolean(), "only a synchronous undefined means default loading");
        Assert.False(runs[0].TryGetProperty("fetches", out _), "nothing may be fetched for a type the loader does not handle");
    }

    [Theory]
    [InlineData("dotnetwasm")]
    [InlineData("assembly")]
    [InlineData("pdb")]
    [InlineData("globalization")]
    public async Task Loader_HandlesEveryRuntimeType(string type)
    {
        JsonElement[] runs = await RunAsync(Scenario("ok", type: type));

        Assert.True(runs[0].GetProperty("returnedPromise").GetBoolean());
        Assert.Equal("resolved", runs[0].GetProperty("outcome").GetString());
        Assert.Null(runs[0].GetProperty("source").GetString());
        Assert.Equal([DefaultUri + ".br"], FetchedUrls(runs[0]));
        Assert.True(runs[0].GetProperty("bodyMatches").GetBoolean());
    }

    [Fact]
    public async Task Loader_IsPassedToBlazorOnlyWhenTheStaticPageAsksForIt()
    {
        JsonElement[] runs = await RunAsync(
            new { checkOnlyOptions = true },
            new { checkOnlyOptions = true, flag = false },
            new { checkOnlyOptions = true, edition = "" });

        Assert.True(runs[0].GetProperty("hasLoader").GetBoolean());
        Assert.Equal("en", runs[0].GetProperty("culture").GetString());
        Assert.False(runs[1].GetProperty("hasLoader").GetBoolean(), "without data-brotli the runtime loads by default");
        Assert.False(runs[2].GetProperty("hasLoader").GetBoolean(), "the server edition never decodes Brotli in the page");
        Assert.Equal("en", runs[1].GetProperty("culture").GetString());
    }
}
