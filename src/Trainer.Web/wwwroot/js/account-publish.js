// Publishes who is signed in on this page (data-owner: a Telegram user id or "anon") under dit.account as
// "owner|unix ms", so island tabs signed in as someone else notice and ask for a reload (AccountWatcher). Loaded by
// signed-in pages and by the sign-in page, where a sign-out lands. An unchanged owner is not rewritten, so it fires no
// storage event.
(function () {
  'use strict';

  var script = document.currentScript;
  var owner = script ? script.getAttribute('data-owner') : null;
  if (!owner || !/^(anon|[1-9][0-9]*)$/.test(owner)) {
    return;
  }

  try {
    var current = window.localStorage.getItem('dit.account');
    var currentOwner = current === null ? null : current.split('|')[0];
    if (currentOwner !== owner) {
      window.localStorage.setItem('dit.account', owner + '|' + Date.now());
    }
  } catch (e) {
    // Storage blocked: there is no other tab to tell.
  }
})();
