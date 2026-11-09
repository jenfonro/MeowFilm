# Film search

`film-search.js` is a synchronous JavaScript resource defining only `globalThis.meowfilmProcess(input)`. It needs no module loader, Node API, network or I/O. Windows embeds this same file as `MeowFilm.FilmSearch.js`; the Swift resource package exposes `FilmSearchProgram.source() throws -> String`. Each native host owns its JSON engine, execution budget and shutdown. The shared search behavior is described in [the common search contract](../../docs/common/film/search.md).

The behavior source is `meowfilm-server` commit `934a03e5aeda7edbcef0cfb037734fed93bfe881`, `Meowfilm/frontend/src/shared/searchRuntime.js`, `tmdbRaw.js` and `normalize.js`. The port includes matching/group/display normalization, regexp cleaning and trailing-digit probes, season/yearbang hints, representative selection, relevance and sorting, TMDB mapping/deduplication, fallback query, detail display fields and episode remarks. It does not call the existing settings-page `film-rules` program: hosts supply the already saved `aggregateRules: string[]`, without a second settings store or defaults.

| operation | input | output |
| --- | --- | --- |
| `site` | `query, aggregateRules, site:{siteKey,siteId,installationId,generation,siteName,order}, page, payload` | `{items,page,pageCount,hasMore,explicitPageCount}` |
| `tmdb` | `query, payload:{results:[...]}` | `{items,pinTmdbFirst}` |
| `fallback` | `query` | `{query}`; empty means no trailing-digit fallback |
| `detail` | `item, payload`, optional `today:"YYYY-MM-DD"` | enhanced item; matching group/score/rank and TMDB identity are preserved |
| `display` | `query, aggregateRules, siteItems, tmdbItems, siteOrder:[siteKey...], searchCoverSite, rawListMode, previousOrder:[displayKey...]` | `{items,order:[displayKey...]}` |

`site` maps the complete valid `payload.list`, accepting the reference `vod_*` fields and their short aliases. It retains original source fields as `siteDetail`, `rawTitle`, `poster`, `textBadge`, `seasonHint` and `seasonHintLabel`. The host supplies a complete, unambiguous installation plus site `siteKey`; `siteId`, `installationId` and `generation` are retained verbatim. An item's `id` and raw-mode `displayKey` encode `[siteKey,siteDetail]` as a JSON tuple. Source order, requested page and row index are separate `siteOrder`, `sitePage`, `seq` fields: there is no fixed 100-row stride. Valid positive integer `pagecount`/`pageCount` metadata takes precedence for `hasMore`; without it, nonempty valid items mean more might exist. Page advancement, repeated-page termination and failure retry belong to the host session.

`tmdb` accepts raw TMDB search structure and stable relative image paths; it retains the first valid item per media type plus normalized group, original ranking and numeric type/ID. `fallback` only transforms the word: the host must call it only after a successful empty raw `results`, and retain the original response if the fallback is also empty. Search failures do not trigger fallback. `detail` receives already fetched raw detail and uses the reference title, image, year and episode-status branches. A missing detail leaves its search item unchanged; the host retains the full raw detail for playback and owns per-type/ID request sharing, success caching and single-detail failure handling.

`display` receives all loaded site and TMDB items, deduplicates site items by full `siteKey` plus original detail ID, and returns normal, site aggregate or TMDB cards. A normal and upgraded site aggregate share `displayKey:"group:<groupKey>"`. TMDB uses its `tmdb:<type>:<id>` identity. TMDB cards include all matched `aggregateItems`, distinct-site count and updated text badge; matched site groups are folded in aggregate mode. Raw mode retains individual site identities and can show them alongside the TMDB card. Source members always retain the original installation/site/generation, detail ID, raw title, remark and season hints; merged site members have `tmdbId:0,tmdbType:""`.

The host saves a separate `order` for each display mode. With no settled previous round, all cards use reference sorting, including first-round streaming updates. Supplying the last **settled** round's `previousOrder` retains those positions, recalculates representatives/members/badges, sorts only new cards and appends them. Every in-flight update in the same round uses the same previous settled order. Switching modes, loading more or entering playback needs no new TMDB request. `query` and `aggregateRules` on `display` are part of the common JSON envelope; mapped items already carry the derived keys and scores, so display does not clean or score them again. The program does not store query sessions or mutate inputs.

There is one deliberate correction to the reference's object-mutation behavior: `buildDisplayedResults` copies normalized TMDB cards before `applyTMDBAggregateIntoCards` mutates the original cached objects. On a first call, the returned cards can therefore omit their sources and new remarks; raw-mode output can depend on whether an earlier aggregate render mutated the cache. This port applies the actual reference aggregate function to the copies it returns, in both modes, so the approved TMDB-with-sources behavior is deterministic and does not mutate caches. The reference comparison separately checks unchanged display fields and the returned aggregate fields against the real reference aggregate function; its output explicitly records this difference.

The other adaptations are the approved complete-page mapping, collision-free client source identities/order and stable appending of later rounds. The original reference's 2,000-episode progress-parser checks are retained as progress-text interpretation, not a result/page limit. The program adds no result limit, year split, similarity threshold, network policy, settings or alternate lifecycle.

Run this package's behavior tests:

```sh
node --test packages/film-search/test/film-search.test.mjs
```

Run the fixed-input comparison against the actual original functions read from the pinned Git commit:

```sh
node packages/film-search/test/compare-server.mjs /root/code/meowfilm-server
```

The comparison loads the original files into an isolated Node VM with I/O substitutes and executes their mapping/search/detail/display functions. It also checks original request fallback/failure behavior to document the host's network boundary; that reference-only execution does not establish a client's request/caching implementation. Complete-page, identity, stable-order and deterministic TMDB-source tests assert public JSON behavior separately. Node checks do not establish native QuickJS execution, platform resource packaging, cancellation, real navigation, a platform build or live TMDB integration; those are integration-session checks.
