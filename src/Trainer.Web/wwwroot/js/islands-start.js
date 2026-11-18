(function () {
  // Read while the script runs; document.currentScript is null afterwards.
  var bootScript = document.currentScript;

  // PageCulture (C#) fixes the runtime's culture from this before the host starts (ADR 0013).
  window.ditPageLanguage = function () {
    return document.documentElement.lang;
  };

  // ClientEditionReader (C#) reads the edition the server wrote on <html>; a missing attribute is the server edition.
  window.ditPageEdition = function () {
    return document.documentElement.dataset.edition || '';
  };

  // LocalTime (C#) formats dates here: the WebAssembly client has no culture data (InvariantGlobalization, ADR 0013).
  window.ditFormatTime = function (language, epochMilliseconds, withTime) {
    // Numeric parts, as .NET's short patterns had them: 08.03.2026, 18:55 in Russian, 3/8/2026, 6:55 PM in English.
    var options = { year: 'numeric', month: 'numeric', day: 'numeric' };
    if (withTime) {
      // 24-hour locales pad the hour (.NET's ru pattern is HH:mm: 01:30, not 1:30); 12-hour ones do not (1:30 AM).
      var cycle = new Intl.DateTimeFormat(language, { hour: 'numeric' }).resolvedOptions().hourCycle;
      options.hour = cycle === 'h23' || cycle === 'h24' ? '2-digit' : 'numeric';
      options.minute = '2-digit';
    }

    return new Intl.DateTimeFormat(language, options).format(new Date(epochMilliseconds));
  };

  // ?perf=1 only (docs/performance.md, "Measure on the deployed site"): once the island is ready, shows the time since
  // navigation start and the bytes transferred for _framework/ files (0 for files served from the cache).
  if (/[?&]perf=1(&|$)/.test(location.search)) {
    // Resolved against <base href>, so the count also works under a path base.
    var frameworkPath = new URL('_framework/', document.baseURI).pathname;
    var observer = new MutationObserver(function () {
      if (!document.querySelector(".island-root[data-island-ready='true']")) {
        return;
      }

      observer.disconnect();
      var ready = Math.round(performance.now());
      var files = performance.getEntriesByType('resource').filter(function (r) {
        return new URL(r.name).pathname.indexOf(frameworkPath) === 0;
      });
      var bytes = files.reduce(function (sum, r) { return sum + r.transferSize; }, 0);
      var readout = document.createElement('p');
      readout.className = 'perf-readout';
      readout.setAttribute('role', 'status');
      // Only the static edition stamps the shell time (ditCountRuntimeStart), because only there the usable moment waits
      // for the index and the progress replay, so the two differ.
      var shell = document.querySelector('.island-root').dataset.islandShellMs;
      readout.textContent = (shell ? 'shell ' + shell + ' ms · usable ' : 'ready ') + ready + ' ms · _framework ' + bytes + ' B in ' + files.length + ' files';
      document.body.appendChild(readout);
    });
    observer.observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-island-ready'] });
  }

  // IslandRoot calls this once per WebAssembly runtime; tests and the Task 21 measurements read the counter.
  window.ditCountRuntimeStart = function () {
    window.__ditStarts = (window.__ditStarts || 0) + 1;

    // The static edition's island is usable only after the content index and the progress replay (spec 5.9); the moment
    // its shell first rendered is kept next to it as data-island-shell-ms, which ?perf=1 shows.
    var root = document.querySelector('.island-root');
    if (document.documentElement.dataset.edition === 'static' && root && !root.dataset.islandShellMs) {
      root.dataset.islandShellMs = String(Math.round(performance.now()));
    }
  };

  // The on-screen keyboard shrinks the layout (interactive-widget=resizes-content), which leaves the field being typed in
  // wherever it was, possibly below the new bottom edge or under the sticky action bar; bring it back into view.
  // scroll-padding-bottom (islands.css) keeps it clear of the bar.
  // Only touch screens have an on-screen keyboard; a desktop window resize is left alone.
  var coarse = window.matchMedia('(pointer: coarse)');
  window.addEventListener('resize', function () {
    if (!coarse.matches) {
      return;
    }

    var field = document.activeElement;
    if (field && (field.tagName === 'TEXTAREA' || (field.tagName === 'INPUT' && /^(text|number|search)$/.test(field.type)))) {
      field.scrollIntoView({ block: 'nearest' });
    }
  });

  // Only a runtime that fails to start (or whose script never loaded) is reported to the reader: the skeleton would stay
  // forever. An error thrown later, in the callback below, is not a failed start and must not cover a working island.
  function showBootFailure(error) {
    console.error('Blazor failed to start', error);
    document.querySelectorAll('.island-skeleton').forEach(function (element) {
      element.hidden = true;
    });
    document.querySelectorAll('.island-boot-error').forEach(function (element) {
      element.hidden = false;
    });

    // Static edition: a failed fetch is most often a page of an older deployment asking for a file that is gone; the
    // inline recovery script (RecoveryScript) offers the reload that fetches current HTML.
    if (document.documentElement.dataset.edition === 'static' && typeof window.ditRecover === 'function') {
      window.ditRecover();
    }
  }

  // Blazor Web resolves Blazor.start() before the WebAssembly runtime starts, so a boot file that is gone (a deployment under
  // an open page) never rejects it: the runtime's own start fails later as an unhandled rejection and the skeleton would stay
  // forever. The text is the one blazor.web.js throws, pinned with the framework version; the browser tests that delete a
  // boot module and an assembly fail first if an upgrade changes it. The listener stays for the page's lifetime and does not
  // call preventDefault, so the console keeps the error. A start that fails this way may also reject Blazor.start, so
  // showBootFailure can run twice; that is intended and safe (it only unhides the same elements, and ditRecover shows its
  // banner once per page and session, the reload parameter ignored).
  window.addEventListener('unhandledrejection', function (event) {
    var reason = event.reason;
    var text = String(reason && reason.message ? reason.message : reason);
    if (text.indexOf('Failed to start platform') >= 0) {
      showBootFailure(reason);
    }
  });

  function started() {
    if (!document.documentElement.dataset.theme) {
      window.ditApplyTheme();
    }

    // Enhanced navigation is off (ADR 0009), so this should never fire; if it ever does, the merged DOM has lost the
    // theme the inline head script set.
    Blazor.addEventListener('enhancedload', function () {
      window.ditApplyTheme();
    });
  }

  // Static edition on GitHub Pages (ADR 0027): the host serves .wasm gzip at best, so the runtime is fetched as the
  // published .br files and decoded here. Content type per boot resource type; any other type loads by default.
  var brotliContentTypes = {
    dotnetwasm: 'application/wasm',
    assembly: 'application/octet-stream',
    pdb: 'application/octet-stream',
    globalization: 'application/octet-stream'
  };

  function sha256Base64(bytes) {
    return crypto.subtle.digest('SHA-256', bytes).then(function (digest) {
      return btoa(String.fromCharCode.apply(null, new Uint8Array(digest)));
    });
  }

  // The sha256 of every runtime file the export published, keyed by boot resource name (ADR 0027). The runtime passes an
  // empty integrity for fingerprinted resources (.NET 10 reads a hash field its boot config does not have), so this table,
  // which the export verifier checks against the published bytes, is what the decoded files are verified against. Loaded
  // once. Without an entry for a file, its decoded bytes are never used (nothing to verify them against), so the file is
  // fetched plain. That plain fetch carries the integrity the runtime passed, which is empty for a fingerprinted resource:
  // the runtime's own default trust, no less than it gives a file it loads without this loader. The export verifier makes a
  // missing entry impossible for any published .br file.
  var bootIntegrity = null;
  var bootIntegrityFailures = 0;

  // A failed import is not kept for the page's lifetime: with no table every runtime file loads uncompressed, a multi-MB
  // regression on a phone, for what may have been one network blip. The table is asked for once more after a short wait. A
  // browser keeps a failed module fetch in its module map, so the retry does not import again: it fetches the fingerprinted
  // file the page's import map gives the specifier, with the integrity the map lists for that URL, bypassing the HTTP cache
  // (which may hold the failed response), and reads the bytes as data in the exporter's exact format (ExportVerifier). The
  // table is the root of trust for the decoded files, so it is never used unverified and never executed: no map entry, no
  // integrity for the URL, a failed fetch (an integrity mismatch included) or a different format all fail the retry. Still
  // failing, the next resource tries again, up to three times in all, and only then is the empty table kept.
  function resolvedFromMap(map, section, target) {
    var entries = map[section] || {};
    for (var specifier in entries) {
      if (Object.prototype.hasOwnProperty.call(entries, specifier) && new URL(specifier, document.baseURI).href === target) {
        return entries[specifier];
      }
    }

    return undefined;
  }

  function parseBootIntegrity(text) {
    var match = /^export default (\{[\s\S]*\});\r?\n?$/.exec(text);
    if (!match) {
      throw new Error('boot-integrity.mjs: unexpected format');
    }

    var table = JSON.parse(match[1]);
    if (!table || typeof table !== 'object' || Array.isArray(table)) {
      throw new Error('boot-integrity.mjs: not an object');
    }

    Object.keys(table).forEach(function (key) {
      if (typeof table[key] !== 'string') {
        throw new Error('boot-integrity.mjs: a value is not a string');
      }
    });

    return table;
  }

  function fetchBootIntegrity() {
    var map = document.querySelector('script[type="importmap"]');
    var parsed = map ? JSON.parse(map.textContent) : {};
    var key = new URL('./boot-integrity.mjs', bootScript && bootScript.src ? bootScript.src : document.baseURI).href;
    var target = resolvedFromMap(parsed, 'imports', key);
    if (typeof target !== 'string') {
      throw new Error('boot-integrity.mjs: not in the import map');
    }

    var url = new URL(target, document.baseURI).href;
    var integrity = resolvedFromMap(parsed, 'integrity', url);
    if (typeof integrity !== 'string' || !integrity) {
      throw new Error('boot-integrity.mjs: the import map lists no integrity for it');
    }

    return fetch(url, { integrity: integrity, cache: 'no-store' }).then(function (response) {
      if (!response.ok) {
        throw new Error(url + ': HTTP ' + response.status);
      }

      return response.text();
    }).then(parseBootIntegrity);
  }

  function importBootIntegrity() {
    return import('./boot-integrity.mjs').then(function (module) {
      return module.default || {};
    });
  }

  var bootIntegrityWarned = false;

  function loadBootIntegrity() {
    if (!bootIntegrity) {
      bootIntegrity = importBootIntegrity().catch(function () {
        return new Promise(function (resolve) {
          setTimeout(resolve, 200);
        }).then(fetchBootIntegrity);
      }).catch(function (error) {
        if (!bootIntegrityWarned) {
          bootIntegrityWarned = true;
          console.warn('Boot integrity table failed to load, runtime files are loaded uncompressed', error);
        }

        bootIntegrityFailures++;
        if (bootIntegrityFailures < 3) {
          bootIntegrity = null;
        }

        return {};
      });
    }

    return bootIntegrity;
  }

  // The table is keyed by the file's path under _framework/ (ru/X.resources.wasm for a satellite assembly): the runtime's
  // name for a resource drops the culture folder, the URL it fetches does not.
  function bootKey(name, defaultUri) {
    var match = /\/_framework\/(.+)$/.exec(new URL(defaultUri, document.baseURI).pathname);
    return match ? match[1] : name;
  }

  // The table wins: the export wrote it from the published bytes. A value from the runtime only fills a gap.
  function integrityOf(table, key, runtimeIntegrity) {
    return Object.prototype.hasOwnProperty.call(table, key) ? String(table[key]) : runtimeIntegrity || '';
  }

  // The runtime reports a boot file it cannot get only after its other downloads are done, and for an HTTP error such as a
  // 404 (a page older than the deployment) it may not report a failed start at all, which would leave the skeleton on the
  // page for ever. So the loader, which knows at once, shows the same failure notice the failed start would. A pdb is only
  // asked for when debugging and is not needed to start.
  function reportMissingBootFile(type, name, failure) {
    if (type !== 'pdb') {
      showBootFailure(failure);
    }
  }

  // A promise that resolves to undefined is not a request for default loading (only a synchronous undefined or null
  // is), so the handled path is a single chain whose catch refetches the uncompressed file itself.
  function loadBootResource(type, name, defaultUri, runtimeIntegrity) {
    if (!Object.prototype.hasOwnProperty.call(brotliContentTypes, type)) {
      return undefined;
    }

    // Default cache mode on purpose: both URLs are content-addressed (the name changes when the bytes change), so a
    // cached copy is never stale, and no-cache would add a revalidation round trip per file to every warm load.
    // Started together so the decoder module loads while the compressed bytes download.
    var compressed = fetch(defaultUri + '.br').then(function (response) {
      if (!response.ok) {
        throw new Error(defaultUri + '.br: HTTP ' + response.status);
      }

      return response.arrayBuffer();
    });
    var decoder = import('./vendor/brotli-decode.mjs');

    return Promise.all([compressed, decoder, loadBootIntegrity()]).then(function (parts) {
      var integrity = integrityOf(parts[2], bootKey(name, defaultUri), runtimeIntegrity);
      var decoded = parts[1].BrotliDecode(new Int8Array(parts[0]));
      var bytes = new Uint8Array(decoded.buffer, decoded.byteOffset, decoded.byteLength);
      var expected = /^sha256-(.+)$/.exec(integrity);
      if (!expected) {
        throw new Error(name + ': no sha256 integrity to verify the decoded bytes against');
      }

      // The browser verifies integrity of what fetch downloads, which here is the .br file, not what the runtime gets.
      return sha256Base64(bytes).then(function (actual) {
        if (actual !== expected[1]) {
          throw new Error(name + ': decoded bytes do not match the integrity value');
        }

        return new Response(bytes, { headers: { 'content-type': brotliContentTypes[type] } });
      });
    }).catch(function (error) {
      console.warn('Brotli boot resource failed, loading ' + name + ' uncompressed', error);
      return loadBootIntegrity().then(function (table) {
        return fetch(defaultUri, { integrity: integrityOf(table, bootKey(name, defaultUri), runtimeIntegrity) }).catch(function (failure) {
          // The browser reports a file that is gone (the page is older than the deployment) as an integrity failure of the
          // error page, a TypeError, where the runtime would have seen a 404; and the page's recovery relies on the runtime's
          // own failure for a missing file. So the file is asked for once more, for its status only: an error status is handed
          // to the runtime as it is, and a good answer is not (the bytes failed the check, so they never reach the runtime
          // and the original failure stands). A HEAD, because the service worker stores any good GET it sees under the
          // file's content-addressed name, and bytes that just failed their integrity check must not be kept there; it never
          // intercepts a HEAD, and none has a body. A host that refuses the method (405, 501) says nothing about the file.
          return fetch(defaultUri, { method: 'HEAD' }).then(function (response) {
            if (response.ok || response.status === 405 || response.status === 501) {
              throw failure;
            }

            return response;
          }, function () {
            throw failure;
          });
        });
      }).then(function (response) {
        if (!response.ok) {
          reportMissingBootFile(type, name, new Error(name + ': HTTP ' + response.status));
        }

        return response;
      }, function (failure) {
        reportMissingBootFile(type, name, failure);
        throw failure;
      });
    });
  }

  var webAssemblyOptions = {
    // The culture is fixed for the runtime's lifetime; switching language reloads the page (spec 8.3).
    applicationCulture: document.documentElement.lang
  };
  if (document.documentElement.dataset.edition === 'static' && bootScript && bootScript.dataset.brotli === 'true') {
    webAssemblyOptions.loadBootResource = loadBootResource;
  }

  try {
    Blazor.start({
      // Every navigation from an island page is a full page load: no runtime outlives its page.
      ssr: { disableDomPreservation: true },
      webAssembly: webAssemblyOptions
    }).then(started, showBootFailure);
  } catch (error) {
    // blazor.web.js did not load, so there is no Blazor to start.
    showBootFailure(error);
  }
})();
