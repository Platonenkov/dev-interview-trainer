(function () {
  // PageCulture (C#) fixes the runtime's culture from this before the host starts (ADR 0013).
  window.ditPageLanguage = function () {
    return document.documentElement.lang;
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
    var observer = new MutationObserver(function () {
      if (!document.querySelector(".island-root[data-island-ready='true']")) {
        return;
      }

      observer.disconnect();
      var ready = Math.round(performance.now());
      var files = performance.getEntriesByType('resource').filter(function (r) {
        return new URL(r.name).pathname.indexOf('/_framework/') === 0;
      });
      var bytes = files.reduce(function (sum, r) { return sum + r.transferSize; }, 0);
      var readout = document.createElement('p');
      readout.className = 'perf-readout';
      readout.setAttribute('role', 'status');
      readout.textContent = 'ready ' + ready + ' ms · _framework ' + bytes + ' B in ' + files.length + ' files';
      document.body.appendChild(readout);
    });
    observer.observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-island-ready'] });
  }

  // IslandRoot calls this once per WebAssembly runtime; tests and the Task 21 measurements read the counter.
  window.ditCountRuntimeStart = function () {
    window.__ditStarts = (window.__ditStarts || 0) + 1;
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
  }

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

  try {
    Blazor.start({
      // Every navigation from an island page is a full page load: no runtime outlives its page.
      ssr: { disableDomPreservation: true },
      // The culture is fixed for the runtime's lifetime; switching language reloads the page (spec 8.3).
      webAssembly: { applicationCulture: document.documentElement.lang }
    }).then(started, showBootFailure);
  } catch (error) {
    // blazor.web.js did not load, so there is no Blazor to start.
    showBootFailure(error);
  }
})();
