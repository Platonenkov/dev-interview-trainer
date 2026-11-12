(function () {
  var button = document.getElementById('theme-toggle');
  if (!button) {
    return;
  }

  button.addEventListener('click', function () {
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
