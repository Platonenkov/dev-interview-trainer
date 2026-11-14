// Public pages only (spec 8.2): the first time a Practice or Mock entry point is hovered, focused or touched, prefetch
// the WebAssembly runtime's first downloads, named in its data-preload-runtime attribute, so the island page starts
// sooner. Once per page; nothing is fetched for visitors who never reach for an island.
(function () {
  'use strict';

  var done = false;

  function preload(event) {
    if (done) {
      return;
    }

    var target = event.target;
    var link = target && target.closest ? target.closest('[data-preload-runtime]') : null;
    if (!link) {
      return;
    }

    done = true;
    ['pointerover', 'focusin', 'touchstart'].forEach(function (type) {
      document.removeEventListener(type, preload, true);
    });
    link.getAttribute('data-preload-runtime').split(/\s+/).forEach(function (url) {
      if (!url) {
        return;
      }

      var hint = document.createElement('link');
      hint.rel = 'prefetch';
      hint.href = url;
      document.head.appendChild(hint);
    });
  }

  // pointerenter and focus do not bubble; their bubbling twins reach one delegated listener for every entry point.
  document.addEventListener('pointerover', preload, true);
  document.addEventListener('focusin', preload, true);
  document.addEventListener('touchstart', preload, { capture: true, passive: true });
})();
