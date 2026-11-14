// Reveals the signed-in page's import notice (AccountControls) while this browser still holds answers given before
// sign-in. It only checks that the anonymous outbox key (the notice's data-key, from StorageKeys) exists: an emptied
// outbox removes the key, and what the key holds is the profile's business. The text is localised by the server.
(function () {
  'use strict';

  var notice = document.querySelector('.import-notice');
  var key = notice ? notice.getAttribute('data-key') : null;
  if (!key) {
    return;
  }

  try {
    if (window.localStorage.getItem(key) !== null) {
      notice.hidden = false;
    }
  } catch (e) {
    // Storage blocked: there is nothing this browser could import.
  }
})();
