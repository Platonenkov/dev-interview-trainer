(function () {
  // Registers the content-addressed service worker (ADR 0026) and tells it which files this page has already loaded, so
  // the first visit fills the worker's cache from the browser's HTTP cache instead of the network. Errors are logged
  // only: a page without a worker is a working page.
  if (!('serviceWorker' in navigator)) {
    return;
  }

  var tag = document.currentScript;
  var scope = tag && tag.dataset.scope;
  var worker = tag && tag.dataset.sw;
  if (!scope || !worker) {
    return;
  }

  var scopeUrl = new URL(scope, location.href);
  var posted = new Set();

  // Only a cheap pre-filter: the worker owns the rule that decides what is content-addressed and ignores the rest. A file the
  // browser was answered with an error (a 404 after a redeployment, a 503) is not posted: the fill would ask the network for
  // it again at once, a second request the page did not make that races whatever the page does next with the same file. A
  // file the page is answered well later (an entry without an error status, or one the browser gave no status for, which is
  // 0) is posted then. responseStatus is the browser's own report of the answer; where it has none the entry is posted.
  function loadedUnderScope() {
    var answeredWell = {};
    var order = [];
    performance.getEntriesByType('resource').forEach(function (entry) {
      var url;
      try {
        url = new URL(entry.name);
      } catch (error) {
        return;
      }

      if (url.origin === scopeUrl.origin && url.pathname.indexOf(scopeUrl.pathname) === 0 && url.search === '' && !posted.has(url.href)) {
        if (!Object.prototype.hasOwnProperty.call(answeredWell, url.href)) {
          order.push(url.href);
          answeredWell[url.href] = false;
        }

        if (!(entry.responseStatus >= 400)) {
          answeredWell[url.href] = true;
        }
      }
    });
    return order.filter(function (href) {
      return answeredWell[href];
    });
  }

  function postFill() {
    var controller = navigator.serviceWorker.controller;
    if (!controller) {
      return;
    }

    var urls = loadedUnderScope();
    if (urls.length === 0) {
      return;
    }

    urls.forEach(function (url) {
      posted.add(url);
    });
    controller.postMessage({ type: 'fill', urls: urls });
  }

  function register() {
    navigator.serviceWorker.register(worker, { scope: scope }).then(postFill).catch(function (error) {
      console.warn('Service worker registration failed', error);
    });
  }

  // On the first visit no worker controls the page yet; the worker claims it after activation.
  navigator.serviceWorker.addEventListener('controllerchange', postFill);
  document.addEventListener('dit:island-usable', postFill);

  if (document.readyState === 'complete') {
    register();
  } else {
    window.addEventListener('load', register);
  }
})();
