(function () {
  // Read synchronously: currentScript is null once the script has run. The prefix is the path base the site is served under;
  // without it the target is relative to this page, which is the site root.
  var base = document.currentScript && document.currentScript.dataset.base ? document.currentScript.dataset.base : '';
  var supported = ['ru', 'en'];
  // Page URLs end with a slash on GitHub Pages; the directory names keep this file free of root-relative literals.
  var directories = { ru: 'ru/', en: 'en/' };

  function stored() {
    try {
      var value = localStorage.getItem('dit.lang');
      return supported.indexOf(value) >= 0 ? value : null;
    } catch (e) {
      // Storage can be blocked; fall back to the browser's languages.
      return null;
    }
  }

  function fromBrowser() {
    var languages = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language];
    for (var i = 0; i < languages.length; i++) {
      var primary = String(languages[i] || '').toLowerCase().split('-')[0];
      if (supported.indexOf(primary) >= 0) {
        return primary;
      }
    }

    return null;
  }

  location.replace(base + directories[stored() || fromBrowser() || 'en']);
})();
