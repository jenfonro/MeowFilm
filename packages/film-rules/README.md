# Film rules

`film-rules.js` defines the pure JSON entry `globalThis.meowfilmProcess(request)`. `playback-rules.js` extends that entry with playback matching, metadata hints, season/episode parsing and quality recognition. Both run on the shared QuickJS-NG 0.18.0 native core through `@meowfilm/script-host/native-json`; the Node owner reuses one dedicated program, Runtime and thread. Third-party script instances have separate lifetimes.

The rule behavior comes from the magic-settings page and save normalization in `meowfilm-server` commit `934a03e5aeda7edbcef0cfb037734fed93bfe881`. Its page processing and save normalization remain the reference. The Node document stores the current decoded rules directly; it does not read native rule documents or encoded rule arrays.

| operation | input | output |
| --- | --- | --- |
| `defaults` | none | `{rules}` |
| `save` | full `rules` | `{rules}` after save normalization |
| `restore` | `section`, full draft `rules` | `{rules}`; selected section restored, other draft edits retained |
| `newMatch` | `pattern`, `replace` | parsed matching row or null |
| `newClean` | `pattern` | normalized cleaning input |
| `process` | `request: {section,text,query?}`, `rules` | rule result for internal computation |
| `preview` | same as `process` | rule result with exact `FilmRuleText` wire values |
| `recognition.batch` | `rules`, `requests` | `{value: [...]}` |
| `recognition.matches` | `rules`, `rows`, `target` | `{value: boolean[]}` |
| `recognition.candidates` | `rules`, normalized `detail`, `target`, `source` | `{value: [...]}` with per-file parsed text/coordinates/quality |

Sections are `episode`, `movie`, `search`. Rules contain `episodeCleanRegexRules: string[]`, `episodeRules: {pattern,replace?,flags?}[]`, `movieRules` of the same shape, and `aggregateRegexRules: string[]`. Initial defaults contain two episode matching rows; restoring the episode section installs three. Saved empty groups remain empty across restart. Saving applies the page's JS trim, the reference handler's Unicode whitespace trim and response decoding; draft preview keeps draft semantics. Invalid regexes may be saved and produce the existing preview errors.

The result retains `type/text`, `inputText`, `processedText`, `matched`, one-based `matchedRuleIndex`, `rewriteApplied`, nullable `rewrittenText`, `groupKey`, `trailingDigits`, `errors` and cumulative `previews`. Output selects `rewrittenText ?? processedText`. First-hit matching, search query-tail protection and deterministic resetting of `g`/`y` expressions are shared by preview and playback. Valid text is a string. A preview string with isolated UTF16 code units is represented by its complete UInt16 array, including nested errors/previews. Rule configuration itself uses Unicode scalar strings, as required by the HTTP bridge.

Playback matching runs its full title chain in QuickJS: NFKC, Unicode case conversion, format controls, aliases, metadata identity, year and season checks, aggregate cleaning and canonical comparison. File-name/path season/episode extraction and regex-based quality inference run there in one detail batch. Node retains provider protocol decoding, accurate resource IDs, hashing, metadata coordinate mapping, scheduling and media ownership.

`recognition.batch` request kinds are `episode`, `season`, `movie`, `search`, `quality`, `metadata` and `match`. Most accept `{kind,text,query?}`. Metadata accepts `{kind:"metadata",record,contentKind:"movie"|"tv"}` and returns `{title,query,year,season}` with null for absent year/season. Matching accepts `{kind:"match",record,target}`. Batches preserve input order and the complete input list. No rule-count, string-length or result-count limit is introduced; native execution budgets remain resource boundaries.

Node integration, persistent ownership and lifecycle are documented in [film-playback](../film-playback/README.md).

```sh
node --test packages/film-rules/test/film-rules.test.mjs
node --test tests/film-rules-service.test.mjs tests/film-playback-recognition.test.mjs
```

The first command keeps the pure-program reference fixtures in an explicit Node vm test context. The second uses the real native program and separately establishes owner/recognition behavior, cancellation and release. Windows addon and iOS NodeMobile verification remain platform integration checks; a Linux test result does not claim those platforms passed.
