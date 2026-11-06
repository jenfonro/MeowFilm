# Film rules

`film-rules.js` is a plain JavaScript resource. It defines `globalThis.meowfilmProcess(request)` and needs no module loader, packages, I/O or script runner. The Windows host embeds it as `MeowFilm.FilmRules.js` and supplies the native JSON engine.

The behavior source is `meowfilm-server` commit `934a03e5aeda7edbcef0cfb037734fed93bfe881`: the magic-settings page in `Meowfilm/frontend/src/pages/dashboard/DashboardPage.vue`, its `shared/episodeMarkerNormalize.js` / `normalize.js` dependencies, and the initial rule tables in `backend/internal/db/defaults.go`. The selected chain is the settings page, including its save/read normalization. It does not use the server playback or search recognition algorithms.

The host operations are:

| operation | input | output |
| --- | --- | --- |
| `initialize` | `document` (null or persisted four string arrays) | `{document, rules, changed}`; empty groups receive database defaults |
| `save` | `rules` | `{document, rules}`; all four groups, with no regexp validation |
| `restore` | `section`, full draft `rules` | `{document, rules}`; selected section restored, whole draft saved |
| `newMatch` | `pattern`, `replace` | parsed matching row or null |
| `newClean` | `pattern` | normalized cleaning input |
| `process` | `request: {section, text, query?}`, `rules` | common processing result |

Sections are `episode`, `movie`, `search`. A rule set contains `episodeCleanRegexRules: string[]`, `episodeRules: {pattern, replace, flags}[]`, `movieRules` of the same shape, and `aggregateRegexRules: string[]`. Matching rows are persisted as encoded JSON strings, retaining the reference distinction between stored data and the decoded page snapshot. The native service owns serialization, snapshots, CAS writes and engine lifetime. The program owns defaults, parsing, normalization, compilation, processing and row previews.

`FilmRuleService.PrepareShutdownAsync(CancellationToken)` pauses admission and cancels/drains all admitted work, including callers waiting for serial execution. An accepted document write still settles with its real receipt and saved snapshot. Cancelling the preparation token cancels only that caller's wait; admission stays paused and subsequent preparation calls wait for the same drain. `Resume()` rejects an unfinished drain and otherwise reopens admission using the same service, engine, snapshot and storage fingerprint. `GetSavedSnapshot()` remains available while prepared. `DisposeAsync()` shares the drain and then releases the engine once; repeated preparation/disposal is safe, and disposal permanently prevents resume. New work while preparing throws `InvalidOperationException`; after disposal starts it throws `ObjectDisposedException`.

`process` is the only execution path for both drafts and saved rules. It returns `type/text` as shown by the reference page, `inputText`, `processedText` (before matching replacement), `matched`, one-based `matchedRuleIndex`, `rewriteApplied`, nullable `rewrittenText`, `groupKey`, `trailingDigits`, `errors` and cumulative `previews`. An earlier invalid match can coexist with a later successful match. Search errors retain partial cleaning and previews. Empty rewriting is a matched error; absent replacement has `rewrittenText: null`. `FilmRuleResult.OutputText` selects rewritten text when present, otherwise processed text.

Reference behavior intentionally retained: initial episode defaults have two rows; section restore has three. Empty groups remain empty after save and refill on the next service initialization. Restore and any row save submit the complete draft. Existing-row matching patterns are not parsed like new `/pattern/flags` input. Movies use the original trimmed filename. Episode processing stops on the first regex hit even when the output is not an episode number. Search tail protection uses the optional query and the two reference probes.

The fixed scenarios in `test/reference-cases.json` cover defaults, first hit, empty/invalid rules, input and replacement normalization, flags, cleaning order/previews, movie year boundaries, search key and tail context. The additional JS tests cover full-panel save, empty-group initialization, read/save decoding, immutable inputs and cache replacement. `FilmRulesTests.cs` shares the fixed scenarios with the real native engine and separately covers service dispatch, CAS failures, real document reopen, cancellation, timeout, preparation/resume and shutdown.

Run only this package's tests:

```sh
node --test packages/film-rules/test/film-rules.test.mjs
```

For migration evidence, read and execute the original page functions against the fixed scenarios:

```sh
node packages/film-rules/test/compare-server.mjs /root/code/meowfilm-server/Meowfilm
```

The reference comparison is a one-off port check, not a runtime dependency or a permanent duplicate of the rule algorithms. Windows native tests and the affected build are owned by the integrating session; a passing Node run does not establish native execution, interruption or resource packaging.
