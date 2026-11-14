# ADR 0011: Catalog search is a GET form whose results are not cached

## Status

Accepted (approved by the owner). Changes spec 8.4 (cache key allow-list) and
spec 11.2 (no form on public pages) as described below.

## Context

Spec 5.1 says tags are "used for search and related questions", and the catalog
needs a search box. Three rules of the spec stand in the way:

- Spec 11.2 allows no form on anonymous public pages. That rule exists because a
  POST form needs an antiforgery token, which sets a cookie and makes the HTML
  per-visitor, so the page could no longer be output-cached and shared.
- Spec 8.4 keys the output cache by path plus an explicit query allow-list
  (`level`, `topic`, `type`, `page`).
- A free-text term is an unbounded set of values: caching its results would let
  anyone fill the cache with one-off entries.

A GET form needs no token and no cookie, so the first reason does not apply to it.

## Decision

- The catalog gets one search form in `FilterPanel`:
  `<form method="get" action="/{culture}/{stack}/questions" role="search">` with a
  labelled `<input type="search" name="q" maxlength="100">`, hidden inputs for the
  current `level`, `topic` and `type` filters (never `page`: a new search starts on
  page one) and a 44 px submit button. It carries no antiforgery token and sets no
  cookie. It is the only form on any anonymous public page; every other public page
  still has none, and POST forms still appear only in signed-in responses and on
  the admin page.
- `CatalogQueryParser.ParseSearch` normalises the term: first `q` only,
  whitespace and control characters collapsed to single spaces, trimmed, at most
  100 characters (never splitting a surrogate pair); nothing left means no search.
  The canonical query string appends `q` last.
- Matching (the Planner's call): a case-insensitive `ILIKE` substring match on the
  question's title in the requested culture, or an exact tag match against the
  term lowercased with spaces turned into hyphens. No full-text search: the
  catalog holds about a thousand questions per culture at most, and Russian
  stemming would need its own text-search configuration. The term is a bound
  parameter (never `FromSqlRaw`) and `%`, `_` and `\` are escaped, so it matches
  literally. The facet counts honour the search. The title is compared under the
  explicit ICU collation `und-x-icu`: plain `ILIKE` folds case with the database's
  `LC_CTYPE`, which is ASCII-only on a database created with `LC_CTYPE C` and would
  silently match Cyrillic case-sensitively (a test builds such a database). The
  collation is deterministic, so `LIKE` works, and it makes the result the same on
  every database; the price is that the server must be built with ICU (the official
  PostgreSQL images are), and without it the query fails loudly instead of
  mismatching. Tags are lowercased in the application, not in SQL.
- The cache key allow-list stays `level`, `topic`, `type`, `page`. A catalog
  request with a non-empty search term bypasses the output cache entirely: no
  lookup, no storage. A request without a term (including `?q=` and a blank term)
  is the same page as before, byte for byte, and stays cached under the same key.
- Uncached search requests are rate limited per client address
  (`App:RateLimits:SearchPerMinute`, fixed one-minute window, default 30, startup
  rejects a non-positive value). This is a new limit: before this ADR no public page
  was limited, because the output cache stood between every public request and the
  database. Only requests that bypass that cache are counted (GET or HEAD to the
  catalog with a non-empty term); cached catalog pages without a term, and every other
  page, are never limited. A request over the limit gets 429, `Retry-After`, and a
  small localised HTML page (not JSON) that is `noindex` and `no-store`. All test
  hosts raise the limit like the existing ones.
- Search results are `noindex` and their canonical link points to themselves, so
  they never compete with the indexable single-selection catalog pages. "Practise
  these questions" ignores the search term.
- Related questions use the same data: up to five live questions of the stack in
  the culture that share a tag with the current one (most shared tags first, then
  the same topic, then title), excluding the question itself and anything retired
  or without a locale for the culture. They are part of the cached anonymous
  question page. A retired question's page shows none: it is a noindex dead end kept
  for old links.

## Consequences

- Every search request that passes the limit hits PostgreSQL. The statement is cheap
  at this size, the term length is bounded and the per-address limit caps the rate; the
  limit is per address, so a distributed flood is a job for the proxy. The address is
  the forwarded client address, so the proxy must send `X-Forwarded-For`. If search
  traffic ever matters, a small bounded cache keyed by the normalised term can be added
  behind the same policy.
- Public pages are no longer form-free: tests assert exactly one GET form on the
  catalog, none elsewhere, no antiforgery token and no `Set-Cookie`.
- Spec 8.4 and 11.2 carry a one-line pointer to this ADR, which supersedes them where
  they differ.
