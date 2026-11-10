# Film playback rules owner

`createFilmRulesService({dataRoot})` is the sole owner of `<dataRoot>/film-rules/rules.json`. It uses the existing trusted runtime data directory, validates its owned path, writes an exclusive private temporary file, flushes it and atomically renames it before publishing. A failed write retains the previous immutable snapshot. External document changes fail with `STORAGE_CHANGED`; malformed current documents fail with `CORRUPT_STORAGE`. Only the current `{rules}` document is read.

The returned methods implement the runtime's `film.rules.*` operations:

| JavaScript method | input | result |
| --- | --- | --- |
| `get()` / `snapshot()` | none | `Promise<{rules,revision}>` |
| `set({rules},{signal}?)` | full draft | saved snapshot |
| `restore({section,rules?},{signal}?)` | selected section and optional full draft | saved snapshot |
| `preview({request,rules?},{signal}?)` | `{section,text,query?}` and optional full draft | existing FilmRuleResult |
| `normalize({kind,pattern,replace?},{signal}?)` | `kind: "match"|"clean"` | existing normalized row/string |
| `clear({}, {signal}?)` | none | default snapshot; removes this owner's document |
| `close()` | none | shared Promise after admitted native work settles and its thread exits |

Draft preview/normalization do not create or modify the document. A first saved snapshot initializes defaults when no document exists. Restore with a supplied draft saves edits in the other sections. Saved empty groups remain empty after reopening. Clear leaves unrelated storage and raw Cat/pan caches intact. Captured old snapshots remain valid after save/restore/clear until owner close. The next cold open after clear initializes defaults.

Snapshots and nested rules are frozen. `revision` includes the exact rules, shared JS program source, QuickJS-NG 0.18.0 commit and Unicode-table identity. Keep the snapshot object itself inside Node: a private WeakMap binds it to the owner's reusable native program. JSON serialization exposes only `{rules,revision}`. Clone/transport projections are for display; consumers use the actual captured snapshot for computation. `invoke(request,{snapshot?,signal}?)` is the owner's internal pure-program call.

Create one owner in runtime composition and inject it as `createPlaybackService({data,library,media,rules,now})`. `await service.open(input)` captures that owner's snapshot and ignores `input.rules`. Search-to-playback composition uses `await service.open(input,{ruleSnapshot})` with the exact snapshot owned by the search session. That argument is internal, never taken from client-supplied rules. Playback snapshots expose `rulesRevision`. Settings/manual/source-display mutations are now asynchronous where they re-evaluate recognition.

`rules.js` exports these internal adapters:

```js
const recognition = createRecognitionRules(ruleSnapshot);
await recognition.batch([
  { kind: 'metadata', record, contentKind: 'tv' },
  { kind: 'match', record, target },
  { kind: 'season', text },
], { signal });
await recognition.season(text, { signal }); // number | undefined
await recognition.episode(text, { signal }); // {season?, episode?}
await recognition.movie(text, { signal }); // boolean
await recognition.search(text, query, { signal }); // {text, key}
```

`recognition.js` exports `await matchesResults(rows,{target,rules:ruleSnapshot,signal})`, the single-row `await matchesResult(row,options)`, and `await recognizeCandidates(detail,{target,source,metadata,rules:ruleSnapshot,signal})`. Whole search pages and detail files are submitted as batches. Metadata hints include normalized query text, year and season, so callers do not repeat those identity calculations on V8. Standalone draft-based adapters create and close a real native program for their batch; production snapshots always reuse the owner.

Pure rule cancellation is separate from finite network producers. Playback cancels/drains admitted calculations on stop/release/close and source invalidation, checks publication generation after asynchronous recognition, and lets the existing data/cache owner settle accepted I/O. A native rejection drains its Runtime before another call rebuilds it. Owner close rejects further admission, interrupts active computation, settles queued operations, and awaits real release. It does not own playback media or data/cache shutdown.

Native `TIMEOUT`, `CANCELLED` and `RESOURCE_LIMIT` retain those public film codes. `INVALID_ARGUMENT` maps to `INVALID_REQUEST`, `SCRIPT_EXCEPTION` to `SCRIPT_FAILURE`, closed native sessions to `CLOSED`, and rejected native artifacts/ABI to `CAPABILITY_UNAVAILABLE`. Invalid regex syntax retains the existing row-level preview error; resource failures are not converted into invalid regex rows.

Late search updates keep valid source results and cursors after a user selects a different metadata provider. For non-group selections, a carrier that no longer identifies the current movie/TV target is ignored, including its display data; it cannot switch the target back. Group late-association and pending/current/user-selection protections are unchanged.
