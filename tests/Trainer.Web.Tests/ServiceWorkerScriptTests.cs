using System.Text.Json;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>
/// The worker (<c>sw-template.js</c> with the rule prepended, as <c>/sw.js</c> serves it) and the page's registration
/// script run under Node against a fake <c>ServiceWorkerGlobalScope</c>, <c>caches</c> and <c>fetch</c> (ADR 0026). Real
/// browser behaviour is checked by the live checks of Task 17.
/// </summary>
public sealed class ServiceWorkerScriptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Scope = "https://example.test/dit/";

    private const string WorkerHarness = """
        const fs = require('fs');
        const vm = require('vm');
        const [scriptPath, name] = process.argv.slice(2);
        const SCOPE = 'https://example.test/dit/';

        function response(url, o) {
          o = o || {};
          const headers = o.headers || {};
          const r = {
            ok: o.ok === undefined ? (o.status === undefined || (o.status >= 200 && o.status < 300)) : o.ok,
            status: o.status === undefined ? 200 : o.status,
            redirected: !!o.redirected,
            type: o.type || 'basic',
            url: o.url === undefined ? url : o.url,
            headers: { get: (key) => (headers[key] === undefined ? null : String(headers[key])) },
            body: o.body || 'bytes',
          };
          r.clone = () => Object.assign({}, r);
          r.blob = async () => ({ size: r.body.length });
          return r;
        }

        function build(options) {
          options = options || {};
          const state = {
            clock: options.clock === undefined ? 1e12 : options.clock,
            fetched: [], puts: [], deletes: [], keysCalls: 0, claimed: 0, skipWaiting: 0, preloadEnabled: 0, warnings: [],
          };
          const stores = new Map();
          function store(cacheName) {
            if (!stores.has(cacheName)) stores.set(cacheName, new Map());
            return stores.get(cacheName);
          }
          const caches = {
            names: [],
            open: async (cacheName) => {
              state.names.push(cacheName);
              const map = store(cacheName);
              const keyOf = (k) => (typeof k === 'string' ? k : k.url);
              return {
                match: async (k) => map.get(keyOf(k)),
                put: async (k, r) => {
                  if (options.putFails && !keyOf(k).endsWith('__recency')) throw new Error('quota');
                  state.puts.push(keyOf(k));
                  map.set(keyOf(k), r);
                },
                keys: async () => { state.keysCalls++; return [...map.keys()].map((url) => ({ url })); },
                delete: async (k) => { state.deletes.push(keyOf(k)); return map.delete(keyOf(k)); },
              };
            },
          };
          state.names = [];
          const listeners = {};
          const sandbox = {
            URL, Promise, Map, Set, Object, Array, Number, JSON, Math, String, Error, console,
            Response: class { constructor(body) { this.body = body; this.ok = true; this.status = 200; this.headers = { get: () => null }; } async json() { return JSON.parse(this.body); } },
            Date: { now: () => state.clock },
            caches,
            fetch: async (input) => {
              const url = typeof input === 'string' ? input : input.url;
              state.fetched.push(url);
              if (options.gate) await options.gate;
              if (options.fetchFails) throw new TypeError('network');
              return (options.respond || ((u) => response(u)))(url);
            },
            location: { href: SCOPE + 'sw.js', origin: 'https://example.test' },
            registration: {
              scope: SCOPE,
              active: options.previousWorker ? {} : null,
              navigationPreload: options.noPreload ? undefined : { enable: async () => { state.preloadEnabled++; } },
            },
            skipWaiting: async () => { state.skipWaiting++; },
            clients: { claim: async () => { state.claimed++; } },
            addEventListener: (type, fn) => { listeners[type] = fn; },
          };
          sandbox.self = sandbox;
          vm.runInNewContext(fs.readFileSync(scriptPath, 'utf8'), sandbox);
          return { state, stores, listeners, sandbox, cacheMap: () => store('dit-immutable-v1:/dit/') };
        }

        function dispatch(world, type, init) {
          const waits = [];
          const event = Object.assign({ waitUntil: (p) => waits.push(Promise.resolve(p)) }, init);
          let responded;
          event.respondWith = (p) => { responded = Promise.resolve(p); };
          world.listeners[type](event);
          return { event, waits, responded: () => responded, settle: () => Promise.all(waits) };
        }

        function request(url, o) {
          o = o || {};
          const headers = o.headers || {};
          return {
            url, method: o.method || 'GET', mode: o.mode || 'no-cors',
            headers: { has: (k) => headers[k.toLowerCase()] !== undefined },
          };
        }

        const NATIVE = SCOPE + '_framework/dotnet.native.8j2kz4qmvx.wasm';
        const SHARD = SCOPE + 'data/ru/q/01J.0123456789abcdef.json';

        async function get(world, url, o) {
          const d = dispatch(world, 'fetch', { request: request(url, o), preloadResponse: o && o.preload });
          const result = d.responded() ? await d.responded() : null;
          await d.settle();
          return { intercepted: !!d.responded(), result };
        }

        const scenarios = {
          async lifecycle() {
            const first = build();
            await dispatch(first, 'install', {}).settle();
            await dispatch(first, 'activate', {}).settle();
            const update = build({ previousWorker: true });
            await dispatch(update, 'install', {}).settle();
            await dispatch(update, 'activate', {}).settle();
            const noPreload = build({ noPreload: true });
            await dispatch(noPreload, 'install', {}).settle();
            await dispatch(noPreload, 'activate', {}).settle();
            return {
              first: { skip: first.state.skipWaiting, claim: first.state.claimed, preload: first.state.preloadEnabled },
              update: { skip: update.state.skipWaiting, claim: update.state.claimed, preload: update.state.preloadEnabled },
              noPreload: { claim: noPreload.state.claimed, preload: noPreload.state.preloadEnabled },
            };
          },

          async cacheFirst() {
            const world = build();
            const miss = await get(world, NATIVE);
            const afterMiss = { fetched: world.state.fetched.length, cached: world.cacheMap().has(NATIVE), name: world.state.names[0] };
            const hit = await get(world, NATIVE);
            return { miss: miss.intercepted, afterMiss, hit: hit.intercepted, fetchedAfterHit: world.state.fetched.length, sameObject: hit.result === world.cacheMap().get(NATIVE) };
          },

          async content() {
            const world = build();
            await get(world, SHARD);
            return { cached: world.cacheMap().has(SHARD) };
          },

          async neverStored() {
            const cases = {
              notFound: (u) => response(u, { status: 404 }),
              unavailable: (u) => response(u, { status: 503 }),
              redirected: (u) => response(u, { redirected: true }),
              opaque: (u) => response(u, { type: 'opaque', ok: false, status: 0 }),
              cors: (u) => response(u, { type: 'cors' }),
              otherUrl: (u) => response(u, { url: SCOPE + '404.html' }),
              emptyUrl: (u) => response(u, { url: '' }),
              partial: (u) => response(u, { status: 206 }),
            };
            const out = {};
            for (const key of Object.keys(cases)) {
              const world = build({ respond: cases[key] });
              const got = await get(world, NATIVE);
              out[key] = { returned: got.result !== null && got.result.url !== undefined, cached: world.cacheMap().has(NATIVE) };
              out[key].status = got.result.status;
            }
            const world = build({ respond: (u) => response(u, { type: 'default' }) });
            await get(world, NATIVE);
            out.defaultType = { cached: world.cacheMap().has(NATIVE) };
            return out;
          },

          async notIntercepted() {
            const world = build();
            const checks = {
              build: SCOPE + 'build.json',
              immutable: SCOPE + 'immutable.json',
              worker: SCOPE + 'sw.js',
              alias: SCOPE + '_framework/blazor.web.js',
              page: SCOPE + 'ru/dotnet/',
              query: NATIVE + '?x=1',
              otherOrigin: 'https://cdn.example.test/dit/_framework/dotnet.native.8j2kz4qmvx.wasm',
              outsideScope: 'https://example.test/other/_framework/dotnet.native.8j2kz4qmvx.wasm',
            };
            const out = {};
            for (const key of Object.keys(checks)) {
              out[key] = (await get(world, checks[key])).intercepted;
            }
            out.post = (await get(world, NATIVE, { method: 'POST' })).intercepted;
            out.range = (await get(world, NATIVE, { headers: { range: 'bytes=0-9' } })).intercepted;
            out.fetched = world.state.fetched.length;
            out.cachedAnything = world.cacheMap().size;
            return out;
          },

          async navigation() {
            const doc = SCOPE + 'ru/dotnet/';
            const preloaded = response(doc, { body: 'preloaded' });
            const w1 = build();
            const withPreload = await get(w1, doc, { mode: 'navigate', preload: Promise.resolve(preloaded) });
            const w2 = build();
            const undefinedPreload = await get(w2, doc, { mode: 'navigate', preload: Promise.resolve(undefined) });
            const w3 = build();
            const rejected = await get(w3, doc, { mode: 'navigate', preload: Promise.reject(new Error('preload')) });
            const w4 = build();
            const post = await get(w4, doc, { mode: 'navigate', method: 'POST' });
            return {
              withPreload: { intercepted: withPreload.intercepted, same: withPreload.result === preloaded, fetched: w1.state.fetched.length },
              undefinedPreload: { intercepted: undefinedPreload.intercepted, fetched: w2.state.fetched, cachedAnything: w2.cacheMap().size },
              rejected: { intercepted: rejected.intercepted, fetched: w3.state.fetched },
              post: post.intercepted,
            };
          },

          async writesAreBestEffort() {
            const world = build({ putFails: true });
            const got = await get(world, NATIVE);
            const fill = dispatch(world, 'message', { data: { type: 'fill', urls: [SHARD] } });
            await fill.settle();
            return { returned: got.result.status, cached: world.cacheMap().size };
          },

          async fill() {
            const world = build();
            await get(world, NATIVE);
            const before = world.state.fetched.length;
            const urls = [
              NATIVE,
              SHARD,
              SCOPE + 'css/site.we5d1p27ih.css',
              SCOPE + 'css/site.we5d1p27ih.css',
              SCOPE + 'build.json',
              SCOPE + 'css/site.css',
              SCOPE + 'css/site.we5d1p27ih.css?x=1',
              'https://other.test/dit/css/site.we5d1p27ih.css',
              42,
              null,
              'http://[bad',
            ];
            const d = dispatch(world, 'message', { data: { type: 'fill', urls } });
            await d.settle();
            const other = dispatch(world, 'message', { data: { type: 'other', urls } });
            const malformed = dispatch(world, 'message', { data: { type: 'fill', urls: 'x' } });
            return {
              waited: d.waits.length,
              ignoredOther: other.waits.length,
              ignoredMalformed: malformed.waits.length,
              newFetches: world.state.fetched.slice(before),
              cached: [...world.cacheMap().keys()].filter((k) => !k.endsWith('__recency')).sort(),
            };
          },

          async fillDropsBadResponses() {
            const world = build({ respond: (u) => response(u, { status: 404 }) });
            const d = dispatch(world, 'message', { data: { type: 'fill', urls: [NATIVE] } });
            await d.settle();
            const failing = build({ fetchFails: true });
            const f = dispatch(failing, 'message', { data: { type: 'fill', urls: [NATIVE] } });
            await f.settle();
            return { cached: world.cacheMap().size, failedFetchCached: failing.cacheMap().size, failedFetches: failing.state.fetched.length };
          },

          async pruneContentByCount() {
            const world = build();
            const urls = [];
            for (let i = 0; i < 605; i++) {
              world.state.clock += 1;
              const url = SCOPE + 'data/ru/q/q' + String(i).padStart(4, '0') + '.0123456789abcdef.json';
              urls.push(url);
              await get(world, url);
            }
            const map = world.cacheMap();
            const count = [...map.keys()].filter((k) => k.includes('/data/')).length;
            return {
              count,
              oldestGone: !map.has(urls[0]),
              boundary: map.has(urls[605 - count]) && !map.has(urls[604 - count]),
              newestSurvives: map.has(urls[604]),
              keysCalls: world.state.keysCalls,
            };
          },

          async pruneAssetsByBytes() {
            const mb = 1024 * 1024;
            const world = build({ respond: (u) => response(u, { headers: { 'Content-Length': 15 * mb } }) });
            const names = ['a', 'b', 'c'].map((n) => SCOPE + 'js/' + n + '.abcdefghij.js');
            for (const url of [names[0], names[1], names[0], names[2]]) {
              world.state.clock += 1000;
              await get(world, url);
            }
            const map = world.cacheMap();
            return { keep: names.map((u) => map.has(u)), fetched: world.state.fetched.length, content: [...map.keys()].filter((k) => k.includes('/data/')).length };
          },

          async noScanPerStoreUnderBudget() {
            const world = build();
            for (let i = 0; i < 12; i++) {
              await get(world, SCOPE + 'js/f' + i + '.abcdefghij.js');
            }
            return { keysCalls: world.state.keysCalls, cached: world.cacheMap().size };
          },

          async contentOverBudgetNeverEvictsRuntime() {
            const world = build();
            await get(world, SCOPE + 'js/keep.abcdefghij.js');
            for (let i = 0; i < 601; i++) {
              world.state.clock += 1;
              await get(world, SCOPE + 'data/ru/q/q' + String(i).padStart(4, '0') + '.0123456789abcdef.json');
            }
            return { runtime: world.cacheMap().has(SCOPE + 'js/keep.abcdefghij.js') };
          },

          async sharedFetchPageThenFill() {
            let release;
            const world = build({ gate: new Promise((resolve) => { release = resolve; }) });
            const page = dispatch(world, 'fetch', { request: request(NATIVE) });
            await new Promise((resolve) => setImmediate(resolve));
            const fill = dispatch(world, 'message', { data: { type: 'fill', urls: [NATIVE] } });
            await new Promise((resolve) => setImmediate(resolve));
            release();
            const response = await page.responded();
            await page.settle();
            await fill.settle();
            return { fetched: world.state.fetched.length, status: response.status, cached: world.cacheMap().has(NATIVE) };
          },

          async sharedFetchFillThenPage() {
            let release;
            const world = build({ gate: new Promise((resolve) => { release = resolve; }) });
            const fill = dispatch(world, 'message', { data: { type: 'fill', urls: [NATIVE] } });
            await new Promise((resolve) => setImmediate(resolve));
            const page = dispatch(world, 'fetch', { request: request(NATIVE) });
            await new Promise((resolve) => setImmediate(resolve));
            release();
            const response = await page.responded();
            await page.settle();
            await fill.settle();
            return { fetched: world.state.fetched.length, status: response.status, cached: world.cacheMap().has(NATIVE) };
          },

          async recencyPersistedOncePerMinute() {
            const world = build();
            const recencyPuts = () => world.state.puts.filter((k) => k.endsWith('__recency')).length;
            await get(world, SCOPE + 'js/a.abcdefghij.js');
            const first = recencyPuts();
            world.state.clock += 10 * 1000;
            await get(world, SCOPE + 'js/b.abcdefghij.js');
            await get(world, SCOPE + 'js/a.abcdefghij.js');
            const within = recencyPuts();
            world.state.clock += 61 * 1000;
            await get(world, SCOPE + 'js/a.abcdefghij.js');
            const after = recencyPuts();
            const record = JSON.parse(world.cacheMap().get('/dit/__recency').body);
            return { first, within, after, keys: Object.keys(record).sort(), shape: Array.isArray(record[SCOPE + 'js/a.abcdefghij.js']) };
          },
        };

        scenarios[name]().then((result) => process.stdout.write(JSON.stringify(result)), (error) => { console.error(error); process.exit(1); });
        """;

    private const string RegisterHarness = """
        const fs = require('fs');
        const vm = require('vm');
        const [scriptPath, name] = process.argv.slice(2);

        function build(options) {
          options = options || {};
          const log = { registered: [], posted: [], warnings: [] };
          const swListeners = {};
          const docListeners = {};
          const winListeners = {};
          const tag = { dataset: options.tagData === undefined ? { scope: '/dit/', sw: '/dit/sw.js' } : options.tagData };
          let controller = options.controller ? { postMessage: (m) => log.posted.push(JSON.parse(JSON.stringify(m))) } : null;
          const sandbox = {
            URL, Promise, Set, Array, JSON, console: { warn: (...a) => log.warnings.push(a.map(String).join(' ')), log() {} },
            location: { href: 'https://example.test/dit/en/dotnet/practice/' },
            performance: { getEntriesByType: () => (options.entries || []).map((entry) => (typeof entry === 'string' ? { name: entry } : entry)) },
            document: {
              currentScript: tag, readyState: options.readyState || 'loading',
              addEventListener: (t, fn) => { docListeners[t] = fn; },
            },
            window: { addEventListener: (t, fn) => { winListeners[t] = fn; } },
          };
          if (!options.noWorkerSupport) {
            sandbox.navigator = {
              serviceWorker: {
                get controller() { return controller; },
                register: (url, o) => {
                  log.registered.push({ url, scope: o.scope });
                  return options.registerFails ? Promise.reject(new Error('denied')) : Promise.resolve({});
                },
                addEventListener: (t, fn) => { swListeners[t] = fn; },
              },
            };
          } else {
            sandbox.navigator = {};
          }
          vm.runInNewContext(fs.readFileSync(scriptPath, 'utf8'), sandbox);
          return {
            log, swListeners, docListeners, winListeners, sandbox,
            setController: () => { controller = { postMessage: (m) => log.posted.push(JSON.parse(JSON.stringify(m))) }; },
            entries: (list) => { options.entries = list; },
          };
        }

        const tick = () => new Promise((resolve) => setImmediate(resolve));
        const ORIGIN = 'https://example.test';
        const EN = [
          ORIGIN + '/dit/css/site.we5d1p27ih.css',
          ORIGIN + '/dit/_framework/dotnet.native.8j2kz4qmvx.wasm',
          ORIGIN + '/dit/en/dotnet/practice/',
          ORIGIN + '/dit/css/site.we5d1p27ih.css?v=1',
          'https://cdn.test/dit/js/x.abcdefghij.js',
          ORIGIN + '/other/js/y.abcdefghij.js',
          ORIGIN + '/dit',
        ];

        const scenarios = {
          async firstVisit() {
            const options = { entries: EN };
            const w = build(options);
            const beforeLoad = { registered: w.log.registered.length, hasLoad: !!w.winListeners.load };
            w.winListeners.load();
            await tick();
            const afterLoad = { registered: w.log.registered.slice(), posted: w.log.posted.length };
            w.setController();
            w.swListeners.controllerchange();
            return { beforeLoad, afterLoad, posted: w.log.posted };
          },

          async returningVisit() {
            const w = build({ entries: EN, controller: true });
            w.winListeners.load();
            await tick();
            return { posted: w.log.posted };
          },

          async islandUsablePostsOnlyNewUrls() {
            const first = [ORIGIN + '/dit/css/site.we5d1p27ih.css'];
            const w = build({ entries: first, controller: true });
            w.winListeners.load();
            await tick();
            w.entries(first.concat([ORIGIN + '/dit/data/ru/q/01J.0123456789abcdef.json']));
            w.docListeners['dit:island-usable']();
            w.docListeners['dit:island-usable']();
            return { posted: w.log.posted };
          },

          async failedLoadsAreNotPosted() {
            const index = ORIGIN + '/dit/data/en/dotnet/index.0123456789abcdef.json';
            const css = ORIGIN + '/dit/css/site.we5d1p27ih.css';
            const script = ORIGIN + '/dit/js/u.abcdefghij.js';
            // responseStatus is 0 when the browser does not say (an older one, or no answer at all): that is not a refusal.
            const w = build({ entries: [{ name: index, responseStatus: 404 }, { name: css, responseStatus: 200 }, { name: script, responseStatus: 0 }], controller: true });
            w.winListeners.load();
            await tick();
            const first = JSON.parse(JSON.stringify(w.log.posted));
            // The page asks again and is answered this time: the file is posted then, not before.
            w.entries([{ name: index, responseStatus: 404 }, { name: index, responseStatus: 200 }, { name: css, responseStatus: 200 }, { name: script, responseStatus: 0 }]);
            w.docListeners['dit:island-usable']();
            return { first, all: w.log.posted };
          },

          async islandUsableWithoutControllerPostsNothing() {
            const w = build({ entries: EN });
            w.docListeners['dit:island-usable']();
            return { posted: w.log.posted.length };
          },

          async registersImmediatelyWhenAlreadyLoaded() {
            const w = build({ readyState: 'complete' });
            await tick();
            return { registered: w.log.registered, hasLoad: !!w.winListeners.load };
          },

          async failuresAreOnlyLogged() {
            const w = build({ registerFails: true, entries: EN, controller: true });
            w.winListeners.load();
            await tick();
            return { warnings: w.log.warnings.length, posted: w.log.posted.length };
          },

          async unsupportedAndUnconfigured() {
            const a = build({ noWorkerSupport: true });
            const b = build({ tagData: {} });
            return { a: { load: !!a.winListeners.load }, b: { load: !!b.winListeners.load, registered: b.log.registered.length } };
          },
        };

        scenarios[name]().then((result) => process.stdout.write(JSON.stringify(result)), (error) => { console.error(error); process.exit(1); });
        """;

    private static async Task<JsonElement> WorkerAsync(string scenario)
    {
        string script = Path.Combine(Path.GetTempPath(), "dit-sw-" + Guid.NewGuid().ToString("N") + ".js");
        string template = await File.ReadAllTextAsync(NodeHarness.WwwRootFile("js", "sw-template.js"), Ct);
        await File.WriteAllTextAsync(script, ServiceWorkerEndpoint.Compose(template), Ct);
        try
        {
            using JsonDocument result = await NodeHarness.RunAsync(WorkerHarness, Ct, script, scenario);
            return result.RootElement.Clone();
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static async Task<JsonElement> RegisterAsync(string scenario)
    {
        using JsonDocument result = await NodeHarness.RunAsync(RegisterHarness, Ct, NodeHarness.WwwRootFile("js", "sw-register.js"), scenario);
        return result.RootElement.Clone();
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task Worker_InstallSkipsWaiting_ActivateAlwaysClaims_AndEnablesPreload()
    {
        JsonElement result = await WorkerAsync("lifecycle");

        Assert.Equal(1, result.GetProperty("first").GetProperty("skip").GetInt32());
        Assert.Equal(1, result.GetProperty("first").GetProperty("claim").GetInt32());
        Assert.Equal(1, result.GetProperty("first").GetProperty("preload").GetInt32());
        Assert.Equal(1, result.GetProperty("update").GetProperty("skip").GetInt32());
        // Claiming is unconditional: names are content-addressed, so it is safe for any generation.
        Assert.Equal(1, result.GetProperty("update").GetProperty("claim").GetInt32());
        Assert.Equal(1, result.GetProperty("update").GetProperty("preload").GetInt32());
        // A browser without navigation preload still activates.
        Assert.Equal(1, result.GetProperty("noPreload").GetProperty("claim").GetInt32());
        Assert.Equal(0, result.GetProperty("noPreload").GetProperty("preload").GetInt32());
    }

    [Fact]
    public async Task Worker_ServesEligibleFilesCacheFirst_FromTheScopedCache()
    {
        JsonElement result = await WorkerAsync("cacheFirst");

        Assert.True(result.GetProperty("miss").GetBoolean());
        Assert.Equal(1, result.GetProperty("afterMiss").GetProperty("fetched").GetInt32());
        Assert.True(result.GetProperty("afterMiss").GetProperty("cached").GetBoolean());
        Assert.Equal("dit-immutable-v1:/dit/", result.GetProperty("afterMiss").GetProperty("name").GetString());
        Assert.True(result.GetProperty("hit").GetBoolean());
        Assert.Equal(1, result.GetProperty("fetchedAfterHit").GetInt32());
        Assert.True(result.GetProperty("sameObject").GetBoolean());
    }

    [Fact]
    public async Task Worker_CachesContentShards()
    {
        JsonElement result = await WorkerAsync("content");

        Assert.True(result.GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task Worker_NeverStoresAFailedRedirectedOpaqueOrForeignResponse_ButReturnsIt()
    {
        JsonElement result = await WorkerAsync("neverStored");

        foreach (string key in new[] { "notFound", "unavailable", "redirected", "opaque", "cors", "otherUrl", "emptyUrl", "partial" })
        {
            Assert.True(result.GetProperty(key).GetProperty("returned").GetBoolean(), key + " must still reach the page");
            Assert.False(result.GetProperty(key).GetProperty("cached").GetBoolean(), key + " must not be cached");
        }

        Assert.Equal(404, result.GetProperty("notFound").GetProperty("status").GetInt32());
        Assert.Equal(503, result.GetProperty("unavailable").GetProperty("status").GetInt32());
        Assert.True(result.GetProperty("defaultType").GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task Worker_LeavesEverythingMutableOrForeignToTheNetwork()
    {
        JsonElement result = await WorkerAsync("notIntercepted");

        foreach (JsonProperty property in result.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            Assert.False(property.Value.GetBoolean(), property.Name + " must not be intercepted");
        }

        Assert.Equal(0, result.GetProperty("fetched").GetInt32());
        Assert.Equal(0, result.GetProperty("cachedAnything").GetInt32());
    }

    [Fact]
    public async Task Worker_NavigationAwaitsPreloadAndFallsBackToFetchOnNothingOrRejection()
    {
        JsonElement result = await WorkerAsync("navigation");

        JsonElement preloaded = result.GetProperty("withPreload");
        Assert.True(preloaded.GetProperty("intercepted").GetBoolean());
        Assert.True(preloaded.GetProperty("same").GetBoolean());
        Assert.Equal(0, preloaded.GetProperty("fetched").GetInt32());
        string page = Scope + "ru/dotnet/";
        Assert.Equal([page], Strings(result.GetProperty("undefinedPreload").GetProperty("fetched")));
        Assert.Equal(0, result.GetProperty("undefinedPreload").GetProperty("cachedAnything").GetInt32());
        Assert.Equal([page], Strings(result.GetProperty("rejected").GetProperty("fetched")));
        Assert.False(result.GetProperty("post").GetBoolean());
    }

    [Fact]
    public async Task Worker_CacheWriteFailureNeverFailsTheResponseOrTheFill()
    {
        JsonElement result = await WorkerAsync("writesAreBestEffort");

        Assert.Equal(200, result.GetProperty("returned").GetInt32());
        Assert.Equal(0, result.GetProperty("cached").GetInt32());
    }

    [Fact]
    public async Task Worker_FillFetchesOnlyEligibleUncachedUrlsOnce()
    {
        JsonElement result = await WorkerAsync("fill");

        Assert.Equal(1, result.GetProperty("waited").GetInt32());
        Assert.Equal(0, result.GetProperty("ignoredOther").GetInt32());
        Assert.Equal(0, result.GetProperty("ignoredMalformed").GetInt32());
        // The native file was cached by the first request; the shard and the stylesheet are new, and the duplicate counts once.
        Assert.Equal(
            [Scope + "css/site.we5d1p27ih.css", Scope + "data/ru/q/01J.0123456789abcdef.json"],
            Strings(result.GetProperty("newFetches")).Order(StringComparer.Ordinal));
        Assert.Equal(
            [Scope + "_framework/dotnet.native.8j2kz4qmvx.wasm", Scope + "css/site.we5d1p27ih.css", Scope + "data/ru/q/01J.0123456789abcdef.json"],
            Strings(result.GetProperty("cached")));
    }

    [Fact]
    public async Task Worker_FillDropsFailedAndBadResponsesWithoutError()
    {
        JsonElement result = await WorkerAsync("fillDropsBadResponses");

        Assert.Equal(0, result.GetProperty("cached").GetInt32());
        Assert.Equal(0, result.GetProperty("failedFetchCached").GetInt32());
        Assert.Equal(1, result.GetProperty("failedFetches").GetInt32());
    }

    [Fact]
    public async Task Worker_PrunesContentShardsPastSixHundredLeastRecentlyUsed_DownToTheLowWaterMark()
    {
        JsonElement result = await WorkerAsync("pruneContentByCount");

        int count = result.GetProperty("count").GetInt32();
        Assert.InRange(count, 540, 600);
        Assert.True(result.GetProperty("oldestGone").GetBoolean());
        Assert.True(result.GetProperty("boundary").GetBoolean());
        Assert.True(result.GetProperty("newestSurvives").GetBoolean());
        // One scan reconciles the record with the cache; going over budget needs no further scan.
        Assert.Equal(1, result.GetProperty("keysCalls").GetInt32());
    }

    [Fact]
    public async Task Worker_ScansTheCacheOncePerLifetime_NotOncePerStore()
    {
        JsonElement result = await WorkerAsync("noScanPerStoreUnderBudget");

        Assert.Equal(1, result.GetProperty("keysCalls").GetInt32());
        Assert.Equal(13, result.GetProperty("cached").GetInt32());
    }

    [Fact]
    public async Task Worker_ContentOverItsBudgetNeverEvictsRuntimeFiles()
    {
        JsonElement result = await WorkerAsync("contentOverBudgetNeverEvictsRuntime");

        Assert.True(result.GetProperty("runtime").GetBoolean());
    }

    [Theory]
    [InlineData("sharedFetchPageThenFill")]
    [InlineData("sharedFetchFillThenPage")]
    public async Task Worker_ShareOneNetworkFetchBetweenAPageRequestAndAFill(string scenario)
    {
        JsonElement result = await WorkerAsync(scenario);

        Assert.Equal(1, result.GetProperty("fetched").GetInt32());
        Assert.Equal(200, result.GetProperty("status").GetInt32());
        Assert.True(result.GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task Worker_PrunesRuntimeAndAssetsToFortyMegabytesLeastRecentlyUsed_AndTouchingProtects()
    {
        JsonElement result = await WorkerAsync("pruneAssetsByBytes");

        // Two files of 15 MB fit; after the first is used again, the third (45 MB in all) evicts the second.
        bool[] keep = [.. result.GetProperty("keep").EnumerateArray().Select(e => e.GetBoolean())];
        Assert.Equal([true, false, true], keep);
        Assert.Equal(3, result.GetProperty("fetched").GetInt32());
        Assert.Equal(0, result.GetProperty("content").GetInt32());
    }

    [Fact]
    public async Task Worker_PersistsRecencyAtMostOncePerMinute()
    {
        JsonElement result = await WorkerAsync("recencyPersistedOncePerMinute");

        Assert.Equal(1, result.GetProperty("first").GetInt32());
        Assert.Equal(1, result.GetProperty("within").GetInt32());
        Assert.Equal(2, result.GetProperty("after").GetInt32());
        Assert.True(result.GetProperty("shape").GetBoolean());
        Assert.Equal([Scope + "js/a.abcdefghij.js", Scope + "js/b.abcdefghij.js"], Strings(result.GetProperty("keys")));
    }

    [Fact]
    public async Task Register_RegistersAfterLoadWithTheScope_AndPostsOnlyAfterTheWorkerControlsThePage()
    {
        JsonElement result = await RegisterAsync("firstVisit");

        Assert.Equal(0, result.GetProperty("beforeLoad").GetProperty("registered").GetInt32());
        Assert.True(result.GetProperty("beforeLoad").GetProperty("hasLoad").GetBoolean());
        JsonElement registered = result.GetProperty("afterLoad").GetProperty("registered").EnumerateArray().Single();
        Assert.Equal("/dit/sw.js", registered.GetProperty("url").GetString());
        Assert.Equal("/dit/", registered.GetProperty("scope").GetString());
        Assert.Equal(0, result.GetProperty("afterLoad").GetProperty("posted").GetInt32());

        JsonElement message = result.GetProperty("posted").EnumerateArray().Single();
        Assert.Equal("fill", message.GetProperty("type").GetString());
        Assert.Equal(
            ["https://example.test/dit/css/site.we5d1p27ih.css", "https://example.test/dit/_framework/dotnet.native.8j2kz4qmvx.wasm", "https://example.test/dit/en/dotnet/practice/"],
            Strings(message.GetProperty("urls")));
    }

    [Fact]
    public async Task Register_ReturningVisitPostsOnceTheRegistrationSettles()
    {
        JsonElement result = await RegisterAsync("returningVisit");

        Assert.Equal(1, result.GetProperty("posted").GetArrayLength());
    }

    [Fact]
    public async Task Register_IslandUsablePostsOnlyUrlsNotPostedBefore()
    {
        JsonElement result = await RegisterAsync("islandUsablePostsOnlyNewUrls");

        JsonElement[] posted = [.. result.GetProperty("posted").EnumerateArray()];
        Assert.Equal(2, posted.Length);
        Assert.Equal(["https://example.test/dit/css/site.we5d1p27ih.css"], Strings(posted[0].GetProperty("urls")));
        Assert.Equal(["https://example.test/dit/data/ru/q/01J.0123456789abcdef.json"], Strings(posted[1].GetProperty("urls")));
    }

    [Fact]
    public async Task Register_NeverPostsAFileTheBrowserWasAnsweredWithAnError_UntilItIsAnsweredWell()
    {
        // The fill asks the network for every posted file the worker does not hold: for a file the page itself was just refused
        // (a 404 after a redeployment, a 503) that is a second request that wins a race with whatever the page does next.
        JsonElement result = await RegisterAsync("failedLoadsAreNotPosted");

        JsonElement[] first = [.. result.GetProperty("first").EnumerateArray()];
        Assert.Single(first);
        Assert.Equal(
            ["https://example.test/dit/css/site.we5d1p27ih.css", "https://example.test/dit/js/u.abcdefghij.js"],
            Strings(first[0].GetProperty("urls")));
        JsonElement[] all = [.. result.GetProperty("all").EnumerateArray()];
        Assert.Equal(2, all.Length);
        Assert.Equal(["https://example.test/dit/data/en/dotnet/index.0123456789abcdef.json"], Strings(all[1].GetProperty("urls")));
    }

    [Fact]
    public async Task Register_IslandUsableWithoutAControllerPostsNothing()
    {
        JsonElement result = await RegisterAsync("islandUsableWithoutControllerPostsNothing");

        Assert.Equal(0, result.GetProperty("posted").GetInt32());
    }

    [Fact]
    public async Task Register_RegistersAtOnceWhenTheDocumentIsAlreadyLoaded()
    {
        JsonElement result = await RegisterAsync("registersImmediatelyWhenAlreadyLoaded");

        Assert.Equal(1, result.GetProperty("registered").GetArrayLength());
        Assert.False(result.GetProperty("hasLoad").GetBoolean());
    }

    [Fact]
    public async Task Register_FailureIsOnlyLogged_AndDoesNothingWithoutSupportOrConfiguration()
    {
        JsonElement failed = await RegisterAsync("failuresAreOnlyLogged");
        JsonElement none = await RegisterAsync("unsupportedAndUnconfigured");

        Assert.Equal(1, failed.GetProperty("warnings").GetInt32());
        Assert.Equal(0, failed.GetProperty("posted").GetInt32());
        Assert.False(none.GetProperty("a").GetProperty("load").GetBoolean());
        Assert.False(none.GetProperty("b").GetProperty("load").GetBoolean());
        Assert.Equal(0, none.GetProperty("b").GetProperty("registered").GetInt32());
    }
}
