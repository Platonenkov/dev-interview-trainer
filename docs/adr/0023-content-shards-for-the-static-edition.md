# ADR 0023: Content shards for the static edition

## Status

Accepted by the owner on 2026-11-17. The shape of the files (an index per culture and stack, one file per question)
is the owner's decision in the static edition design; the mechanism below is an implementation detail of Plan 5.

## Context

The server edition hands an island its questions through `/api/sessions/...`. GitHub Pages (ADR 0022) has no API,
no response headers and no server-side session, so an island has to read the questions as plain files. One file with
every question would make a visitor download all of them to practise one topic; the owner ruled out a first download
of about 500 KB. The files also have to survive a deployment: a visitor holding a page of build A must still be
able to read its questions after build B is live, and Pages cannot set `Cache-Control`.

## Decision

1. Shape. For each culture and stack there is `data/{culture}/{stack}/index.{hash}.json`, with the format version, the
   culture, the stack, the hash of the content import, the topic titles (ordered by key, ordinal, so the bytes do not depend on the build
   machine's collation) and one entry per live question (`id`, `slug`,
   `topic`, `level`, `type`, `title` and the hash of its file), ordered by id (ordinal). Each live question with a locale
   for the culture has `data/{culture}/q/{id}.{hash}.json`, which is exactly the `QuestionDto` a server session returns.
   The island derives a question's path from the index entry with `StaticContentPaths.Question`, so the host and the
   island cannot disagree about a name.
2. Mechanism. `StaticSiteQueries` reads the imported rows and builds each question with `SessionRows.ToDto`, the code
   behind a session, so the HTML in a file is the importer's rendered and sanitised HTML and a shard cannot drift from a
   session. `StaticContentCatalog` serialises with the source-generated `TrainerJsonContext` (compact, camel case, fixed
   order, no timestamps), names every file after the first 16 lowercase hex characters of the SHA-256 of its bytes
   (`StaticContentHash`) and keeps the result for the active content hash; the composite cache invalidator clears it
   after each import. Identical content therefore gives identical bytes and names, and a name changes only when its
   bytes do. The one encoder setting is that Cyrillic is written as is (Russian files stay near their UTF-8 size) while
   `<`, `>`, `&` and quotes stay escaped.
3. Serving. `StaticContentEndpoints` maps `GET /data/{lang}/{stackKey}/index.{hash}.json` and
   `GET /data/{lang}/q/{id}.{hash}.json` in the static edition only; the export crawls them. Only the current name
   answers; any other name, including the name of a previous build, is a 404 with an empty body and `Content-Length: 0`.
4. Pointing at the files. The island page bootstrap carries `StaticSite.IndexPath`, relative to the base URL with no
   leading slash, so a page and its index always come from the same build, and `IssuesNewUrl` for "Report a problem".
   `App:StaticSite:RepositoryUrl` (https, default the project repository) supplies the issue URL.
5. Retention and "site updated". The export lists every content file in `immutable.json`, and the next deployment
   carries the previous build's files alongside its own (design 5.13). A file that is still gone, because two builds
   went out in between, is a 404 that the island reports as "site updated" instead of a broken page.
6. The same pass lists the pages to crawl (`IStaticSiteQueries.GetPagePathsAsync`): the sitemap entries of every culture
   plus the pages of retired questions, which stay readable, and separately the paths of renamed slugs, which answer a 301.

## Consequences

- Every content change gives a new index name in both cultures of every stack, because the index carries the content
  hash. Question files change only for the questions whose bytes changed.
- The index is about 200 bytes per question, far below the owner's limit; a question file is a few kilobytes.
- A stack whose key is `q` would collide with the question route, so the content validator rejects it (`stack-key-reserved`).
- The server edition is untouched: no endpoint, no catalog and no `StaticSite` value in its bootstrap.
- Unlike the HTML pages, the content files are immutable per name, so the service worker and Pages' own caching are
  safe for them.
