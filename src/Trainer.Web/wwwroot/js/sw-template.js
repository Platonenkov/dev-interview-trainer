// Service worker of the static edition (ADR 0026). The host prepends `const ELIGIBLE = /.../;`, the one rule that says
// which paths under the scope are content-addressed (StaticAssetRules). A name that matches always means the same bytes,
// so a cached copy is never stale and there is no generation, precache list or version to keep in step with a build.

const SCOPE_PATH = new URL(self.registration.scope).pathname;
const CACHE_NAME = 'dit-immutable-v1:' + SCOPE_PATH;

// Runtime and assets are bounded by bytes, content shards by count: a shard is small and numerous, a runtime file large.
const ASSET_BUDGET_BYTES = 40 * 1024 * 1024;
const CONTENT_BUDGET_ENTRIES = 600;
const RECENCY_KEY = SCOPE_PATH + '__recency';
const RECENCY_PERSIST_MS = 60 * 1000;
// A prune evicts down to this share of a budget, so a cache at its limit does not prune again on the next store.
const LOW_WATER = 0.9;
const FILL_CONCURRENCY = 4;
const FILL_MAX_URLS = 500;

// url -> [last use in ms, size in bytes]; held in memory and written out at most once a minute.
let recency = null;
let recencyLoad = null;
let recencyDirty = false;
let lastPersist = 0;
let pruneChain = Promise.resolve();
// False until one scan has made the recency map mirror the cache; after that the map alone says whether a prune is due.
let reconciled = false;
// url -> promise of an unread copy of the network response; a second request for the same URL clones it instead of fetching.
const inflight = new Map();

// The path under the scope of a same-origin, query-free URL that the rule accepts, or null.
function eligiblePath(urlText) {
  let url;
  try {
    url = new URL(urlText, self.location.href);
  } catch (error) {
    return null;
  }

  if (url.origin !== self.location.origin || url.search !== '' || !url.pathname.startsWith(SCOPE_PATH)) {
    return null;
  }

  const path = url.pathname.slice(SCOPE_PATH.length);
  return ELIGIBLE.test(path) ? path : null;
}

function storable(response, requestedUrl) {
  return response.ok
    && response.status === 200
    && !response.redirected
    && (response.type === 'basic' || response.type === 'default')
    && response.url === requestedUrl;
}

function loadRecency(cache) {
  if (recency) {
    return Promise.resolve(recency);
  }

  recencyLoad = recencyLoad || (async () => {
    const loaded = new Map();
    try {
      const stored = await cache.match(RECENCY_KEY);
      if (stored) {
        const record = await stored.json();
        for (const key of Object.keys(record)) {
          const value = record[key];
          if (Array.isArray(value) && typeof value[0] === 'number' && typeof value[1] === 'number') {
            loaded.set(key, value);
          }
        }
      }
    } catch (error) {
      // A missing or damaged record only makes every entry count as the oldest.
    }

    recency = loaded;
    return loaded;
  })();
  return recencyLoad;
}

async function persistRecency(cache, force) {
  if (!recency || !recencyDirty || (!force && Date.now() - lastPersist < RECENCY_PERSIST_MS)) {
    return;
  }

  lastPersist = Date.now();
  recencyDirty = false;
  try {
    await cache.put(RECENCY_KEY, new Response(JSON.stringify(Object.fromEntries(recency)), {
      headers: { 'Content-Type': 'application/json' },
    }));
  } catch (error) {
    recencyDirty = true;
  }
}

async function sizeOf(response) {
  const length = Number(response.headers.get('Content-Length'));
  if (Number.isFinite(length) && length > 0) {
    return length;
  }

  try {
    return (await response.clone().blob()).size;
  } catch (error) {
    return 0;
  }
}

async function remember(cache, url, size) {
  const times = await loadRecency(cache);
  times.set(url, [Date.now(), size]);
  recencyDirty = true;
}

async function touch(cache, url) {
  const times = await loadRecency(cache);
  const known = times.get(url);
  times.set(url, [Date.now(), known ? known[1] : 0]);
  recencyDirty = true;
}

function isContent(url) {
  const path = eligiblePath(url);
  return path !== null && path.startsWith('data/');
}

function overBudget(times) {
  let entries = 0;
  let bytes = 0;
  for (const [url, value] of times) {
    if (isContent(url)) {
      entries += 1;
    } else {
      bytes += value[1];
    }
  }

  return entries > CONTENT_BUDGET_ENTRIES || bytes > ASSET_BUDGET_BYTES;
}

// One scan per worker lifetime: entries the record does not know count as oldest, entries it holds for files that are gone are dropped.
async function reconcile(cache, times) {
  const present = new Set();
  for (const request of await cache.keys()) {
    if (eligiblePath(request.url) === null) {
      continue;
    }

    present.add(request.url);
    if (!times.has(request.url)) {
      const response = await cache.match(request, { ignoreVary: true });
      times.set(request.url, [0, response ? await sizeOf(response) : 0]);
      recencyDirty = true;
    }
  }

  for (const url of [...times.keys()]) {
    if (!present.has(url)) {
      times.delete(url);
      recencyDirty = true;
    }
  }

  reconciled = true;
}

async function pruneNow(cache) {
  const times = await loadRecency(cache);
  if (!reconciled) {
    await reconcile(cache, times);
  }

  if (!overBudget(times)) {
    return;
  }

  const assets = [];
  const content = [];
  for (const [url, value] of times) {
    (isContent(url) ? content : assets).push({ url, last: value[0], size: value[1] });
  }

  const oldestFirst = (a, b) => a.last - b.last;
  content.sort(oldestFirst);
  assets.sort(oldestFirst);
  let count = content.length;
  let bytes = assets.reduce((sum, entry) => sum + entry.size, 0);
  const drop = async (entry) => {
    await cache.delete(entry.url);
    times.delete(entry.url);
    recencyDirty = true;
  };

  // Each budget is judged on its own: content over its limit never evicts runtime files, and the reverse.
  for (const entry of count > CONTENT_BUDGET_ENTRIES ? content : []) {
    if (count <= CONTENT_BUDGET_ENTRIES * LOW_WATER) {
      break;
    }

    await drop(entry);
    count -= 1;
  }

  for (const entry of bytes > ASSET_BUDGET_BYTES ? assets : []) {
    if (bytes <= ASSET_BUDGET_BYTES * LOW_WATER) {
      break;
    }

    await drop(entry);
    bytes -= entry.size;
  }
}

// One prune at a time; a failure never reaches the page.
function prune(cache) {
  pruneChain = pruneChain.then(() => pruneNow(cache)).catch(() => {});
  return pruneChain;
}

// Best effort: a quota or write error must not fail the response the page already has.
async function store(url, copy) {
  try {
    const cache = await caches.open(CACHE_NAME);
    // Measured before the put, which consumes the body.
    const size = await sizeOf(copy);
    await cache.put(url, copy);
    await remember(cache, url, size);
    await prune(cache);
    await persistRecency(cache, false);
  } catch (error) {
    // Dropped; the next request goes to the network again.
  }
}

async function cacheFirst(event, request, url) {
  try {
    const cache = await caches.open(CACHE_NAME);
    const hit = await cache.match(url, { ignoreVary: true });
    if (hit) {
      event.waitUntil(touch(cache, url).then(() => persistRecency(cache, false)).catch(() => {}));
      return hit;
    }
  } catch (error) {
    // Storage unavailable: behave as if there were no worker.
  }

  const pending = inflight.get(url);
  if (pending) {
    try {
      const shared = await pending;
      if (shared) {
        return shared.clone();
      }
    } catch (error) {
      // The other fetch failed; this request tries for itself.
    }
  }

  const response = await fetchOnce(url, () => fetch(request));
  if (storable(response, url)) {
    event.waitUntil(store(url, response.clone()));
  }

  return response;
}

// Registers the fetch under its URL while it runs, so a fill or a page request for the same file waits for it.
async function fetchOnce(url, start) {
  let settle;
  inflight.set(url, new Promise((resolve) => { settle = resolve; }));
  try {
    const response = await start();
    settle(response.clone());
    return response;
  } catch (error) {
    settle(null);
    throw error;
  } finally {
    inflight.delete(url);
  }
}

async function navigate(event) {
  try {
    const preloaded = await event.preloadResponse;
    if (preloaded) {
      return preloaded;
    }
  } catch (error) {
    // A failed preload falls back to a plain fetch below.
  }

  return fetch(event.request);
}

async function fillOne(cache, url) {
  if (inflight.has(url) || await cache.match(url, { ignoreVary: true })) {
    return;
  }

  try {
    const response = await fetchOnce(url, () => fetch(url));
    if (storable(response, url)) {
      const size = await sizeOf(response);
      await cache.put(url, response.clone());
      await remember(cache, url, size);
    }
  } catch (error) {
    // Dropped; a later page visit posts the URL again.
  }
}

async function fill(urls) {
  const wanted = [];
  const seen = new Set();
  for (const candidate of urls.slice(0, FILL_MAX_URLS)) {
    if (typeof candidate !== 'string') {
      continue;
    }

    let url;
    try {
      url = new URL(candidate, self.location.href).href;
    } catch (error) {
      continue;
    }

    if (!seen.has(url) && eligiblePath(url) !== null) {
      seen.add(url);
      wanted.push(url);
    }
  }

  if (wanted.length === 0) {
    return;
  }

  try {
    const cache = await caches.open(CACHE_NAME);
    let next = 0;
    const lanes = Array.from({ length: Math.min(FILL_CONCURRENCY, wanted.length) }, async () => {
      while (next < wanted.length) {
        const url = wanted[next++];
        await fillOne(cache, url);
      }
    });
    await Promise.all(lanes);
    await prune(cache);
    await persistRecency(cache, false);
  } catch (error) {
    // Best effort.
  }
}

self.addEventListener('install', (event) => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener('activate', (event) => {
  // Safe for every generation: names are content-addressed, so a claimed page still gets the bytes it names.
  event.waitUntil(Promise.all([
    self.clients.claim(),
    self.registration.navigationPreload ? self.registration.navigationPreload.enable() : undefined,
  ]));
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') {
    return;
  }

  if (request.mode === 'navigate') {
    event.respondWith(navigate(event));
    return;
  }

  if (request.headers.has('range') || eligiblePath(request.url) === null) {
    return;
  }

  event.respondWith(cacheFirst(event, request, request.url));
});

self.addEventListener('message', (event) => {
  const data = event.data;
  if (data && data.type === 'fill' && Array.isArray(data.urls)) {
    event.waitUntil(fill(data.urls));
  }
});
