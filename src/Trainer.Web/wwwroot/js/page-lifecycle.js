(function () {
  // Tells an island that the page is being hidden or left, so it can save what it still holds in memory (the Mock
  // island's debounced text). visibilitychange comes first and leaves the most time; pagehide covers a close or a
  // navigation that skips it. The save is best effort: a write still waiting for its lock may not finish.
  var handlers = new Map();
  var nextHandle = 1;

  function notify() {
    handlers.forEach(function (dotnetRef) {
      dotnetRef.invokeMethodAsync('OnPageHidden');
    });
  }

  function onVisibilityChange() {
    if (document.visibilityState === 'hidden') {
      notify();
    }
  }

  function attach(dotnetRef) {
    var handle = nextHandle++;
    if (handlers.size === 0) {
      document.addEventListener('visibilitychange', onVisibilityChange);
      window.addEventListener('pagehide', notify);
    }

    handlers.set(handle, dotnetRef);
    return handle;
  }

  function detach(handle) {
    handlers.delete(handle);
    if (handlers.size === 0) {
      document.removeEventListener('visibilitychange', onVisibilityChange);
      window.removeEventListener('pagehide', notify);
    }
  }

  // Answers that exist only in this tab's memory (storage refused them) would be lost by leaving the page, so the browser
  // asks first. The listener exists only while that is true: a beforeunload listener keeps some browsers from putting
  // the page in the back/forward cache.
  var unsaved = false;

  function onBeforeUnload(event) {
    event.preventDefault();
    event.returnValue = '';
  }

  function setUnsaved(value) {
    var next = value === true;
    if (next === unsaved) {
      return;
    }

    unsaved = next;
    if (unsaved) {
      window.addEventListener('beforeunload', onBeforeUnload);
    } else {
      window.removeEventListener('beforeunload', onBeforeUnload);
    }
  }

  window.ditPageLifecycle = { attach: attach, detach: detach, setUnsaved: setUnsaved };
})();
