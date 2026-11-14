// Lock-protected localStorage documents for the islands (window.ditStore). Every read-modify-write runs under Web
// Locks on every key it touches: the keys are computed up front, sorted and each acquired once, nested in that order,
// so two ops on the same keys in opposite directions cannot deadlock. Inside the locks the op reads, applies and writes
// in one synchronous task. Without navigator.locks nothing is written at all: an unlocked shared read-modify-write
// could lose another tab's answers, so the client keeps them in memory instead (StoreErrors.NoLocks).
(function () {
  'use strict';

  var LOCK_PREFIX = 'dit-store:';
  var CORRUPT_PREFIX = 'dit.outbox.v1.corrupt.';
  var VERSION = 1;

  // The nesting limit of the client's JsonDocument (StoredDocument.MaxDepth): deeper values are corrupt on read and
  // never written, so both sides always agree on what a stored value is.
  var MAX_DEPTH = 64;
  var TOO_DEEP = { name: 'TooDeep' };

  var SHAPES = {
    outbox: { list: 'answers', id: 'clientAnswerId' },
    summaries: { list: 'items', id: 'mockSessionId' }
  };

  // Key families (StorageKeys). Every op checks its keys against its family before taking any lock, so no op can write a
  // key of another family or one the app does not own. The owner itself is not checked: the script does not know who
  // is signed in, so moving between owners is the caller's decision.
  var OUTBOX_KEY = /^dit\.outbox\.v1\.(anon|u\.[1-9][0-9]*)$/;
  var SUMMARY_KEY = /^dit\.mock\.v1\.(pending|failed)\.[1-9][0-9]*$/;
  var PROGRESS_KEY = /^dit\.mock\.v1\.(anon|[1-9][0-9]*)\.active$/;
  var QUESTIONS_KEY = /^dit\.mock\.v1\.(anon|[1-9][0-9]*)\.[0-9A-HJKMNP-TV-Z]{26}\.questions$/;
  var PREFERENCE_KEY = /^dit\.(clock\.v1|practice\.v1\.filters)$/;

  // Which family a list shape lives in: a move must stay inside one family.
  var SHAPE_KEYS = { outbox: OUTBOX_KEY, summaries: SUMMARY_KEY };

  function own(table, name) {
    return typeof name === 'string' && Object.prototype.hasOwnProperty.call(table, name) ? table[name] : null;
  }

  function inFamily(key, families) {
    return families.some(function (family) { return family.test(key); });
  }

  // Whole-value writes only where the last writer may win (preferences) or the value is written once (questions);
  // quarantine only for keys the stores read as documents (never dit.account or a quarantined copy).
  var ALLOWED = {
    append: function (op) { return OUTBOX_KEY.test(op.key); },
    remove: function (op) { return OUTBOX_KEY.test(op.key); },
    summaryAdd: function (op) { return SUMMARY_KEY.test(op.key); },
    summaryRemove: function (op) { return SUMMARY_KEY.test(op.key); },
    move: function (op) {
      var family = own(SHAPE_KEYS, op.shape == null ? 'outbox' : op.shape);
      return family !== null && typeof op.to === 'string' && family.test(op.key) && family.test(op.to);
    },
    putIfRevision: function (op) { return PROGRESS_KEY.test(op.key); },
    put: function (op) { return inFamily(op.key, [PREFERENCE_KEY, QUESTIONS_KEY]); },
    'delete': function (op) { return inFamily(op.key, [PROGRESS_KEY, QUESTIONS_KEY]); },
    quarantine: function (op) { return inFamily(op.key, [OUTBOX_KEY, SUMMARY_KEY, PROGRESS_KEY, QUESTIONS_KEY, PREFERENCE_KEY]); }
  };

  function result(ok, persisted, error, extra) {
    var value = { ok: ok, persisted: persisted, error: error || null, revision: null, count: 0 };
    if (extra) {
      Object.assign(value, extra);
    }

    return value;
  }

  function done(count, extra) {
    return result(true, true, null, Object.assign({ count: count }, extra));
  }

  function failed(error) {
    return result(false, false, error);
  }

  function storage() {
    try {
      return window.localStorage || null;
    } catch (e) {
      return null;
    }
  }

  function writeError(e) {
    if (e === TOO_DEEP) {
      return 'bad-op';
    }

    // Chromium and WebKit name it QuotaExceededError; Firefox reports NS_ERROR_DOM_QUOTA_REACHED (code 1014).
    return e && (e.name === 'QuotaExceededError' || e.code === 22 || e.code === 1014) ? 'quota' : 'write-failed';
  }

  // True when a container sits deeper than MAX_DEPTH levels (the root object is level 1).
  function tooDeep(value, depth) {
    if (value === null || typeof value !== 'object') {
      return false;
    }

    if (depth >= MAX_DEPTH) {
      return true;
    }

    var children = Array.isArray(value) ? value : Object.keys(value).map(function (k) { return value[k]; });
    for (var i = 0; i < children.length; i++) {
      if (tooDeep(children[i], depth + 1)) {
        return true;
      }
    }

    return false;
  }

  // Every write goes through here: a value the client could not read back is refused before anything is stored.
  function setDocument(s, key, value) {
    if (tooDeep(value, 0)) {
      throw TOO_DEEP;
    }

    s.setItem(key, JSON.stringify(value));
  }

  // The same rules as StoredDocument.Parse in the client.
  function classify(raw) {
    if (raw === null) {
      return { state: 'missing', doc: null };
    }

    var doc;
    try {
      doc = JSON.parse(raw);
    } catch (e) {
      return { state: 'corrupt' };
    }

    if (doc === null || typeof doc !== 'object' || Array.isArray(doc) || !Number.isInteger(doc.version) || tooDeep(doc, 0)) {
      return { state: 'corrupt' };
    }

    if (doc.version !== VERSION) {
      return { state: 'newer' };
    }

    // A non-integer revision is corrupt here (not only in putIfRevision), so quarantine sets it aside instead of
    // putIfRevision failing forever.
    if ((doc.answers !== undefined && !Array.isArray(doc.answers))
      || (doc.items !== undefined && !Array.isArray(doc.items))
      || (doc.revision !== undefined && !Number.isInteger(doc.revision))) {
      return { state: 'corrupt' };
    }

    return { state: 'valid', doc: doc };
  }

  function refusal(classified) {
    return failed(classified.state === 'newer' ? 'newer-version' : 'corrupt');
  }

  function readList(s, key, shape) {
    var classified = classify(s.getItem(key));
    if (classified.state === 'corrupt' || classified.state === 'newer') {
      return classified;
    }

    var doc = classified.doc || { version: VERSION };
    if (doc[shape.list] === undefined) {
      doc[shape.list] = [];
    }

    return { state: 'valid', doc: doc, list: doc[shape.list] };
  }

  function idOf(entry, shape) {
    var id = entry !== null && typeof entry === 'object' ? entry[shape.id] : undefined;
    return typeof id === 'string' ? id : null;
  }

  function hasIds(entries, shape) {
    return Array.isArray(entries) && entries.every(function (e) { return idOf(e, shape) !== null; });
  }

  // Entries with a known id are replaced in place; new ones are appended.
  function merge(list, entries, shape) {
    var index = new Map();
    list.forEach(function (entry, i) {
      var id = idOf(entry, shape);
      if (id !== null && !index.has(id)) {
        index.set(id, i);
      }
    });
    entries.forEach(function (entry) {
      var id = idOf(entry, shape);
      if (index.has(id)) {
        list[index.get(id)] = entry;
      } else {
        index.set(id, list.length);
        list.push(entry);
      }
    });
  }

  function without(list, ids, shape) {
    var remove = new Set(ids);
    return list.filter(function (entry) { return !remove.has(idOf(entry, shape)); });
  }

  // Throws on failure; the stored value is then unchanged. An empty list removes the key.
  function writeList(s, key, doc, shape) {
    if (doc[shape.list].length === 0) {
      s.removeItem(key);
    } else {
      setDocument(s, key, doc);
    }
  }

  function append(s, op, shape, entries) {
    if (!hasIds(entries, shape)) {
      return failed('bad-op');
    }

    var target = readList(s, op.key, shape);
    if (target.state !== 'valid') {
      return refusal(target);
    }

    merge(target.list, entries, shape);
    writeList(s, op.key, target.doc, shape);
    return done(entries.length);
  }

  function remove(s, op, shape) {
    if (!Array.isArray(op.ids) || !op.ids.every(function (id) { return typeof id === 'string'; })) {
      return failed('bad-op');
    }

    var target = readList(s, op.key, shape);
    if (target.state !== 'valid') {
      return refusal(target);
    }

    // Only named ids go: entries no id can name (null, non-object or id-less) are never swept.
    var kept = without(target.list, op.ids, shape);
    var removed = target.list.length - kept.length;
    if (removed > 0) {
      target.doc[shape.list] = kept;
      writeList(s, op.key, target.doc, shape);
    }

    return done(removed);
  }

  // The ids held by outbox keys other than the two a move touches. Read without their locks: an id only ever leaves an
  // owner's outbox once the server has stored it, and then moving it again could not change whose answer it is.
  function claimedElsewhere(s, op) {
    var claimed = new Set();
    for (var i = 0; i < s.length; i++) {
      var key = s.key(i);
      if (key === op.key || key === op.to || !OUTBOX_KEY.test(key)) {
        continue;
      }

      var other = readList(s, key, SHAPES.outbox);
      if (other.state === 'valid') {
        other.list.forEach(function (entry) {
          var id = idOf(entry, SHAPES.outbox);
          if (id !== null) {
            claimed.add(id);
          }
        });
      }
    }

    return claimed;
  }

  // Destination first: merge into `to`, then remove exactly the moved ids from the source. A failure between the two
  // writes leaves the entries in both keys (persisted, not ok); the next move removes them from the source. An outbox
  // entry another owner's outbox already holds (left by such a half-finished move into that owner) is not moved again,
  // only removed from the source, so one answer can never sit in two owners' outboxes.
  function move(s, op) {
    var shape = own(SHAPES, op.shape == null ? 'outbox' : op.shape);
    if (!shape || (op.ids != null && !Array.isArray(op.ids))) {
      return failed('bad-op');
    }

    if (op.key === op.to) {
      return done(0);
    }

    var source = readList(s, op.key, shape);
    if (source.state !== 'valid') {
      return refusal(source);
    }

    var target = readList(s, op.to, shape);
    if (target.state !== 'valid') {
      return refusal(target);
    }

    var only = op.ids != null ? new Set(op.ids) : null;
    var claimed = shape === SHAPES.outbox ? claimedElsewhere(s, op) : new Set();
    var selected = source.list.filter(function (entry) {
      var id = idOf(entry, shape);
      return id !== null && (only === null || only.has(id));
    });
    var moving = selected.filter(function (entry) { return !claimed.has(idOf(entry, shape)); });
    if (selected.length === 0) {
      return done(0);
    }

    if (op.value) {
      moving = moving.map(function (entry) { return Object.assign({}, entry, op.value); });
    }

    if (moving.length > 0) {
      merge(target.list, moving, shape);
      try {
        writeList(s, op.to, target.doc, shape);
      } catch (e) {
        return failed(writeError(e));
      }
    }

    source.doc[shape.list] = without(source.list, selected.map(function (entry) { return idOf(entry, shape); }), shape);
    try {
      writeList(s, op.key, source.doc, shape);
    } catch (e) {
      return result(false, true, writeError(e), { count: moving.length });
    }

    return done(moving.length);
  }

  function putIfRevision(s, op) {
    if (!op.value || !Number.isInteger(op.expectedRevision)) {
      return failed('bad-op');
    }

    var classified = classify(s.getItem(op.key));
    if (classified.state === 'corrupt' || classified.state === 'newer') {
      return refusal(classified);
    }

    var current = classified.doc && classified.doc.revision !== undefined ? classified.doc.revision : 0;
    // A document of another mock session is a conflict at any revision: a stale tab must not overwrite it.
    var otherSession = typeof op.sessionId === 'string' && classified.doc && classified.doc.sessionId !== op.sessionId;
    if (current !== op.expectedRevision || otherSession) {
      return result(false, false, 'revision', { revision: current });
    }

    setDocument(s, op.key, op.value);
    return done(1, { revision: op.value.revision });
  }

  function put(s, op) {
    if (op.value === undefined) {
      return failed('bad-op');
    }

    // A corrupt value is simply replaced; a newer app's value is not.
    if (classify(s.getItem(op.key)).state === 'newer') {
      return failed('newer-version');
    }

    setDocument(s, op.key, op.value);
    return done(1);
  }

  function deleteKey(s, op) {
    var classified = classify(s.getItem(op.key));
    if (classified.state === 'missing') {
      return done(0);
    }

    if (classified.state !== 'valid') {
      return refusal(classified);
    }

    if (typeof op.sessionId === 'string' && classified.doc.sessionId !== op.sessionId) {
      return done(0);
    }

    s.removeItem(op.key);
    return done(1);
  }

  // Sets an unparseable value aside: copy first, delete only if the copy succeeded, so nothing is ever lost. Every
  // quarantine also holds the lock on the corrupt prefix, so two tabs never pick the same copy name.
  function quarantine(s, op) {
    var raw = s.getItem(op.key);
    if (raw === null || classify(raw).state !== 'corrupt') {
      return done(0);
    }

    var stamp = Date.now();
    while (s.getItem(CORRUPT_PREFIX + stamp) !== null) {
      stamp++;
    }

    s.setItem(CORRUPT_PREFIX + stamp, raw);
    s.removeItem(op.key);
    return done(1);
  }

  var OPS = {
    append: function (s, op) { return append(s, op, SHAPES.outbox, op.value); },
    remove: function (s, op) { return remove(s, op, SHAPES.outbox); },
    move: move,
    summaryAdd: function (s, op) { return append(s, op, SHAPES.summaries, [op.value]); },
    summaryRemove: function (s, op) { return remove(s, op, SHAPES.summaries); },
    putIfRevision: putIfRevision,
    put: put,
    'delete': deleteKey,
    quarantine: quarantine
  };

  function lockedKeys(op) {
    var keys = op.op === 'move' ? [op.key, op.to] : op.op === 'quarantine' ? [op.key, CORRUPT_PREFIX] : [op.key];
    return Array.from(new Set(keys)).sort();
  }

  // Acquires keys[i..] one inside the other; the innermost callback runs with every lock held.
  function withLocks(keys, i, run) {
    if (i === keys.length) {
      return run();
    }

    return navigator.locks.request(LOCK_PREFIX + keys[i], function () {
      return withLocks(keys, i + 1, run);
    });
  }

  async function mutate(opJson) {
    var op;
    try {
      op = JSON.parse(opJson);
    } catch (e) {
      return JSON.stringify(failed('bad-op'));
    }

    var apply = op !== null && typeof op === 'object' ? own(OPS, op.op) : null;
    if (!apply || typeof op.key !== 'string' || !ALLOWED[op.op](op)) {
      return JSON.stringify(failed('bad-op'));
    }

    if (!navigator.locks) {
      return JSON.stringify(failed('no-locks'));
    }

    var s = storage();
    if (!s) {
      return JSON.stringify(failed('no-storage'));
    }

    var outcome = await withLocks(lockedKeys(op), 0, function () {
      try {
        return apply(s, op);
      } catch (e) {
        return failed(writeError(e));
      }
    });
    return JSON.stringify(outcome);
  }

  function read(key) {
    var s = storage();
    try {
      return s ? s.getItem(key) : null;
    } catch (e) {
      return null;
    }
  }

  // The account hand-over between tabs (AccountWatcher). The value is "owner|unix ms", last writer wins, no lock
  // needed. An unchanged owner is not rewritten, so tabs of the same account stay quiet.
  var ACCOUNT_KEY = 'dit.account';
  var accountWatchers = new Map();
  var nextWatcher = 1;

  function parseAccount(value) {
    if (typeof value !== 'string' || value.length === 0) {
      return null;
    }

    var bar = value.indexOf('|');
    var time = bar < 0 ? 0 : Number(value.slice(bar + 1));
    return { owner: bar < 0 ? value : value.slice(0, bar), time: Number.isFinite(time) ? time : 0 };
  }

  function readAccount(s) {
    try {
      return s ? parseAccount(s.getItem(ACCOUNT_KEY)) : null;
    } catch (e) {
      return null;
    }
  }

  function publishAccount(owner) {
    var s = storage();
    var current = readAccount(s);
    try {
      if (s && (!current || current.owner !== String(owner))) {
        s.setItem(ACCOUNT_KEY, String(owner) + '|' + Date.now());
      }
    } catch (e) {
      // Without storage there is no other tab to tell; the server's owner check still applies.
    }
  }

  // The page's claim: when another owner was published after this page started loading, the cookie the page was
  // rendered with is gone, so that owner is returned and nothing is written (overwriting it would make the other tab
  // see a change that is not one). An older value is stale and is replaced, and so is one dated in the future: after the
  // device clock moved backwards, such a value would otherwise count as "newer" on every load until the clock caught up.
  function claimAccount(owner) {
    var current = readAccount(storage());
    if (current && current.owner !== String(owner) && current.time >= performance.timeOrigin && current.time <= Date.now()) {
      return current.owner;
    }

    publishAccount(owner);
    return null;
  }

  // The storage event fires only in the other tabs of the origin, never in the writer.
  function watchAccount(dotnetRef) {
    var handle = nextWatcher++;
    var listener = function (event) {
      var published = event.key === ACCOUNT_KEY ? parseAccount(event.newValue) : null;
      if (published) {
        dotnetRef.invokeMethodAsync('OnAccountPublished', published.owner);
      }
    };
    window.addEventListener('storage', listener);
    accountWatchers.set(handle, listener);
    return handle;
  }

  function unwatchAccount(handle) {
    var listener = accountWatchers.get(handle);
    if (listener) {
      window.removeEventListener('storage', listener);
      accountWatchers.delete(handle);
    }
  }

  window.ditStore = {
    mutate: mutate,
    read: read,
    publishAccount: publishAccount,
    claimAccount: claimAccount,
    watchAccount: watchAccount,
    unwatchAccount: unwatchAccount
  };
})();
