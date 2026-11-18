(function () {
  // Static edition: the page holds every question and this script hides the ones the query string does not select.
  // It repeats the server's catalog rules (CatalogQueryParser, CatalogQueries) so both editions answer a query alike.
  var MAX_SEARCH_LENGTH = 100;

  // char.IsWhiteSpace or char.IsControl in .NET; \s in JavaScript differs (it includes U+FEFF).
  var BLANK = /[\u0000-\u0020\u007f-\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/;

  var form = document.querySelector('form[data-catalog-filter]');
  if (!form) {
    return;
  }

  // Same normalisation as CatalogQueryParser.ParseSearch: runs of blanks become one space, the ends are trimmed, the
  // term is cut to 100 UTF-16 units and never ends on half of a surrogate pair.
  function normaliseSearch(raw) {
    if (!raw) {
      return '';
    }

    var out = '';
    var pendingSpace = false;
    for (var i = 0; i < raw.length; i++) {
      var c = raw.charAt(i);
      if (BLANK.test(c)) {
        pendingSpace = out.length > 0;
        continue;
      }

      if (out.length + (pendingSpace ? 2 : 1) > MAX_SEARCH_LENGTH) {
        break;
      }

      if (pendingSpace) {
        out += ' ';
        pendingSpace = false;
      }

      out += c;
    }

    if (out.length > 0) {
      var last = out.charCodeAt(out.length - 1);
      if (last >= 0xd800 && last <= 0xdbff) {
        out = out.slice(0, -1);
      }
    }

    return out;
  }

  // The values the page offers for one parameter, by lower-case spelling; an unknown value is dropped, as on the server.
  // `extra` adds known values that have no checkbox: a topic of the stack with no live question here is still a
  // selection on the server (it matches nothing), so it must empty the list rather than be ignored.
  function knownValues(name, extra) {
    var known = Object.create(null);
    for (var e = 0; e < extra.length; e++) {
      if (extra[e] !== '') {
        known[extra[e].toLowerCase()] = extra[e];
      }
    }

    var boxes = form.querySelectorAll('input[type="checkbox"][name="' + name + '"]');
    for (var i = 0; i < boxes.length; i++) {
      known[boxes[i].value.toLowerCase()] = boxes[i].value;
    }

    return known;
  }

  function selection(params, name, known) {
    var chosen = Object.create(null);
    var count = 0;
    var values = params.getAll(name);
    for (var i = 0; i < values.length; i++) {
      var value = known[values[i].toLowerCase()];
      if (value !== undefined && !chosen[value]) {
        chosen[value] = true;
        count++;
      }
    }

    return { values: chosen, any: count > 0 };
  }

  // An empty dimension is no filter; within one dimension the values are alternatives.
  function allows(chosen, value) {
    return !chosen.any || chosen.values[value] === true;
  }

  function tagsOf(row) {
    var raw = row.getAttribute('data-tags') || '';
    return raw === '' ? [] : raw.split(' ');
  }

  // Substring of the lower-case title, or a tag spelt exactly (spaces become hyphens), as CatalogQueries.ApplySearch.
  function matchesSearch(row, term) {
    if (term === '') {
      return true;
    }

    // toLowerCase on the term, ToLowerInvariant on the server's data-title: both agree on the ru/en content.
    var lower = term.toLowerCase();
    if ((row.getAttribute('data-title') || '').indexOf(lower) !== -1) {
      return true;
    }

    return tagsOf(row).indexOf(lower.split(' ').join('-')) !== -1;
  }

  // The filters are read from the query string alone, so a reload or a shared link shows the same rows; `_r` (the
  // refresh parameter of the import flow) and `page` are never looked at.
  var params = new URLSearchParams(window.location.search);
  var term = normaliseSearch(params.get('q'));
  var levels = selection(params, 'level', knownValues('level', []));
  var topics = selection(params, 'topic', knownValues('topic', (form.getAttribute('data-known-topics') || '').split(' ')));
  var types = selection(params, 'type', knownValues('type', []));

  var boxes = form.querySelectorAll('input[type="checkbox"]');
  for (var b = 0; b < boxes.length; b++) {
    var chosen = { level: levels, topic: topics, type: types }[boxes[b].name];
    boxes[b].checked = chosen !== undefined && chosen.values[boxes[b].value] === true;
  }

  // A shared link must show which filters are active, so the collapsed drawer opens when any box is ticked.
  var drawer = form.querySelector('details');
  if (drawer && form.querySelector('input[type="checkbox"]:checked')) {
    drawer.open = true;
  }

  var search = form.querySelector('input[name="q"]');
  if (search) {
    search.value = term;
  }

  var rows = document.querySelectorAll('[data-level]');
  var visible = 0;
  for (var r = 0; r < rows.length; r++) {
    var row = rows[r];
    var show = allows(levels, row.getAttribute('data-level'))
      && allows(topics, row.getAttribute('data-topic'))
      && allows(types, row.getAttribute('data-type'))
      && matchesSearch(row, term);
    var item = row.closest('li') || row;
    item.hidden = !show;
    if (show) {
      visible++;
    }
  }

  var found = document.querySelector('[data-found-template]');
  if (found) {
    found.textContent = found.getAttribute('data-found-template').split('{0}').join(String(visible));
  }

  var empty = document.querySelector('[data-catalog-empty]');
  if (empty) {
    empty.hidden = visible > 0;
  }
})();
