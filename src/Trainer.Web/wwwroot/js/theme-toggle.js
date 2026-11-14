(function () {
  // Delegated, so the toggle keeps working however the header is (re)rendered, on public and island pages alike.
  document.addEventListener('click', function (event) {
    var button = event.target instanceof Element ? event.target.closest('#theme-toggle') : null;
    if (!button) {
      return;
    }

    var root = document.documentElement;
    var next = root.dataset.theme === 'light' ? 'dark' : 'light';
    root.dataset.theme = next;
    try {
      localStorage.setItem('theme', next);
    } catch (e) {
      // Storage can be blocked; the choice then lasts until the page is reloaded.
    }
  });
})();
