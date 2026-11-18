(function () {
  // Delegated, so the choice is recorded however the header is (re)rendered. The key is a plain preference: last writer wins.
  document.addEventListener('click', function (event) {
    var link = event.target instanceof Element ? event.target.closest('[data-lang-choice]') : null;
    if (!link) {
      return;
    }

    try {
      localStorage.setItem('dit.lang', link.getAttribute('data-lang-choice'));
    } catch (e) {
      // Storage can be blocked; the page still opens in the chosen language.
    }
  });
})();
