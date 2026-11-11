# Novel rules

`novel-rules.js` is one synchronous, application-owned JSON program defining
`globalThis.meowfilmProcess`. The program has no Node, network, storage or browser
dependency. The workspace package `@meowfilm/novel-rules` exports `novelRuleSource`
from `index.js`, which reads the fixed application-owned program. The source
module's Node owner executes it with `NativeJsonProgram`; both clients use
`source.runtime.rules.process {request}` (`POST /v1/source/rules.process`) and do
not embed a second engine or a SwiftPM resource. Native owners keep book IDs,
sessions, rule configuration, persistence and layout.

| operation | input | result |
| --- | --- | --- |
| `identity` | `book` | `{name,author,key}` |
| `merge` | `books` | `{books:[representative + groupKey + candidates]}` |
| `matchChapter` | `chapters,anchor` | `{index,requiresSelection}` |
| `validateRule` | `rule` | `{valid,errors}` |
| `transform` | `title,blocks,book,source,rules,conversion` | `{title,blocks}` |
| `matchUrl` | `pattern,url` | boolean, true only when the original JavaScript match covers the complete URL |

Books use `installationId`, `bookUrl`, `name`, `author`. Both known normalized name
and author are required to group across sources. Unknown authors remain separate
per installation and book URL, independent of arrival order. Repeated source
results update that candidate in place. The returned matching key is not a
persistent book ID: enriching an author must not rename a native saved record.

Chapter matching first uses a unique URL, then a unique normalized title, excluding
volume headers. Chinese chapter numbers and fullwidth/punctuation differences
normalize, but duplicate or unmatched titles require explicit user selection.
Chapter indices are array positions. There is no silent same-index fallback.

Replacement fields follow the Reader management model: `pattern`, `replacement`,
`isRegex`, `isEnabled`, `scopeTitle`, `scopeContent`, `scope`, `excludeScope`,
`timeoutMillisecond`, `order`, `name`, `group`, and the native-owned `id`.
Empty replacement is valid. Rules execute in ascending order, preserving input
order for ties. Regular expressions use JavaScript global replacement (including
capture substitutions); literal replacement treats `$` literally. Scope and
exclusion accept comma, semicolon or newline-separated exact source URLs or
normalized book names. Empty scope means all; exclusion wins. Consecutive text
blocks are paragraphs joined with newlines for replacement, including
cross-paragraph matches; changed text is split back into paragraphs. Images keep
their URLs, metadata and relative order unchanged and separate text runs. A rule
change can therefore change block indices; the native reader resolves or
invalidates its saved layout anchor against the changed content.

The Node owner receives the complete rule list and invokes each enabled rule
with its own declared deadline (default 3000 ms), using the existing QuickJS
interrupt on the execution thread. Positive safe-integer budgets have no added
business maximum; there is no fixed deadline for the whole transform. It returns
no transformed chapter until every step succeeds. JavaScript elapsed checks
additionally reject late results, but are not a replacement for native
interruption. After rule application the owner invokes with empty rules to apply
`conversion`: `none`, `simplified` or `traditional`, with a separate 3000 ms budget.
Other novel operations default to 3000 ms; `matchUrl` uses 2000 ms. Requests contain
only the six fixed operations, never program source.

Invalid transform input or enabled rules return `INVALID_REQUEST`; validation
itself preserves `{valid,errors}`. Timeouts remain `TIMEOUT`, cancellation remains
`CANCELLED`, and closed owners return `CLOSED`. A failed native invocation is fully
released before a new invocation recreates the program; close rejects queued
work and waits for active native resources and all admitted transforms. Native
readers publish results only while their page/session is current. These failures
do not silently discard a rule or overwrite raw content.

Identity normalization and the fixed simplified/traditional character pairs are
adapted from the same-owner `meowfilm-server` Reader at
`934a03e5aeda7edbcef0cfb037734fed93bfe881`, `src/utils/bookIdentity.js` and
`src/utils/chinese.js`. This is not the deferred user dictionary feature.
See [the common novel contract](../../docs/common/novel/reading.md) and
[third-party notices](../../LICENSES/NOTICE.md).

```sh
node --test packages/novel-rules/test/novel-rules.test.mjs
```

These package tests verify pure JSON behavior. `tests/source-rules.test.mjs` covers
the fixed RPC, shared fixtures on the real native engine, declared/default
budgets, cancellation, failure recovery and close. The explicit native budget
and queue cases live in `packages/script-host/test/native-json.test.js`.
These do not verify packaged platform navigation, persisted native data or
platform rendering.
`test/fixtures.json` also supplies both native test suites with the same input and
expected purified display blocks. It is test data, not a shipped rule collection.
