(function () {
  // Keyboard shortcuts of the question screen (spec 9.1): 1-4 pick, Enter submits, N goes next. The island decides
  // what a key means (KeyboardShortcuts.Resolve); this script only filters out what must stay native.
  var handlers = new Map();
  var nextHandle = 1;

  var TEXT_INPUT_TYPES = ['text', 'search', 'email', 'url', 'tel', 'password', 'number', 'date', 'datetime-local',
    'month', 'time', 'week'];

  function isTextEntry(target) {
    if (!(target instanceof Element)) {
      return false;
    }

    if (target.closest('textarea, select, [contenteditable]:not([contenteditable="false"])')) {
      return true;
    }

    return target instanceof HTMLInputElement && TEXT_INPUT_TYPES.indexOf(target.type) >= 0;
  }

  // Enter on these activates them natively (a hint button, a link, a details summary); a submit must not ride along.
  function activatesOnEnter(target) {
    return target instanceof Element && target.closest('button, a[href], summary, [role="button"], [role="link"]') !== null;
  }

  // The physical key, so N and the digits work on every layout (a Russian layout gives "т" for KeyN).
  function shortcutOf(event) {
    var digit = /^(?:Digit|Numpad)([1-4])$/.exec(event.code || '');
    if (digit) {
      return digit[1];
    }

    if (/^[1-4]$/.test(event.key)) {
      return event.key;
    }

    if (event.key === 'Enter') {
      return 'enter';
    }

    // The key value only when the event has no physical code (some on-screen keyboards): on layouts such as Dvorak the
    // letter n sits on another key, and KeyN alone keeps exactly one key meaning Next.
    if (event.code === 'KeyN' || (!event.code && (event.key === 'n' || event.key === 'N'))) {
      return 'next';
    }

    return null;
  }

  function onKeyDown(event) {
    if (event.ctrlKey || event.altKey || event.metaKey || event.isComposing || event.repeat || event.defaultPrevented) {
      return;
    }

    if (isTextEntry(event.target)) {
      return;
    }

    var key = shortcutOf(event);
    if (key === null || (key === 'enter' && activatesOnEnter(event.target))) {
      return;
    }

    if (key === 'enter') {
      // Enter on a focused radio or checkbox would otherwise do nothing useful; the island submits instead.
      event.preventDefault();
    }

    handlers.forEach(function (dotnetRef) {
      dotnetRef.invokeMethodAsync('OnShortcut', key);
    });
  }

  function attach(dotnetRef) {
    var handle = nextHandle++;
    if (handlers.size === 0) {
      document.addEventListener('keydown', onKeyDown);
    }

    handlers.set(handle, dotnetRef);
    return handle;
  }

  function detach(handle) {
    handlers.delete(handle);
    if (handlers.size === 0) {
      document.removeEventListener('keydown', onKeyDown);
    }
  }

  window.ditShortcuts = { attach: attach, detach: detach };
})();
