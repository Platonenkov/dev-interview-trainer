// Read by goatcounter.js (the self-hosted, reduced GoatCounter script, docs/adr/0025), which must load after this file.
// The path is the page path alone. goatcounter.js also cuts any query or hash off whatever this returns and never sends the
// query string in another field, so a catalog search term does not leave the browser.
window.goatcounter = {
  path: function () {
    return location.pathname;
  }
};
