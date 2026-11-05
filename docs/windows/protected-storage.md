# Windows local documents and sensitive storage

The App uses the `aggregate` data directory and the exact namespaces declared by `AppIdentity.NativeNamespaces`. Only `AppIdentity.ProtectedNamespaces` (`music.accounts.v1`, `music.platform-context.v1`) use CurrentUser DPAPI. App assembly passes this list through `AppDefinition` to the single `LocalDocumentStore` implementation. Ordinary business records are raw JSON bytes; complete script sources are raw `.js` files.

Native owners use the store directly. The optional script process has no ordinary business-document namespace grant; bridge access is limited by its separate exact namespace grants. The `ProtectedDocumentBridge` name and wire transport remain unchanged.

## Public store API (MeowFilm.Platform.Storage)

```csharp
public sealed record DocumentFingerprint(bool Exists, int Length, string? Sha256)
{
    public static DocumentFingerprint Missing { get; }
    public static DocumentFingerprint FromBytes(ReadOnlySpan<byte> bytes);
}
public sealed record DocumentSnapshot(byte[]? Data, DocumentFingerprint Fingerprint);

public interface IDocumentStore : IAsyncDisposable
{
    Task<DocumentSnapshot> ReadAsync(string documentNamespace,
        CancellationToken cancellationToken = default);
    Task<DocumentFingerprint> WriteAsync(string documentNamespace,
        ReadOnlyMemory<byte> data, DocumentFingerprint expected,
        CancellationToken cancellationToken = default);
}

public LocalDocumentStore(string productRoot, IEnumerable<string> allowedNamespaces,
    IEnumerable<string>? allowedCollections = null,
    IEnumerable<string>? protectedNamespaces = null);
```

- One opaque business document per exact namespace, at most **8 MiB** whether plaintext or protected. The store does not decode/normalize JSON. The separate **16 MiB** source-file limit applies only to collection members, not business documents or bridge transfers.
- Root must already be a dedicated, app-owned directory on a local fixed/removable volume with a nonzero directory file identity. Constructor validates/pins it without creating directories/files or calling DPAPI. Product owns root selection, ACL policy and lifetime; the App continues using its existing aggregate root. Do not supply a shared/world-writable or roaming/cloud-synchronized root.
- Namespace lists contain 0–64 unique case-sensitive entries. Syntax: 1–128 ASCII characters, first alphanumeric, remainder alphanumeric/period/underscore/hyphen, no `..`. `protectedNamespaces` must be a subset of the exact `allowedNamespaces`; omission means no DPAPI namespaces. Prefix matches do not grant access or protection. Invalid names fail before storage access; valid unregistered names return `Unavailable`.
- `allowedCollections` is the trusted-native source-file grant: a member is exactly `collection.<64 lowercase hex>`, with no chunk index. The App grants `music.script-sources.v2`. The suffix is an import-content reference/update identity, not a read-time content check. Ordinary exact namespace grants take precedence if a grant overlaps a collection member. Collections do not become bridge grants.
- `Data == null` means genuinely absent. An existing zero-byte file has `Exists=true`, `Length=0`, the empty-content SHA-256, and a non-null empty array. Caller owns returned bytes. Reads use the current file length; there is no historical length parameter or persisted plaintext checksum envelope.
- Every write needs an exact base fingerprint (`Missing` only for an absent file). The store computes the current file's existence/length/SHA-256 and compares/writes while holding the same root-wide lock. These are content CAS fingerprints, **not monotonic revisions**; identical-content A→B→A cannot be distinguished. Editing a plaintext file changes its next fingerprint without causing an integrity refusal. An outdated write base returns `Conflict`.
- Write takes an owned byte snapshot before its first asynchronous suspension. There is no pending-operation count quota. One operation executes at a time for atomic CAS, and blocking native/filesystem work runs off the caller's UI thread. Cancelled queued reads are retired; accepted writes are not cancelled; close waits for actual operations before releasing root handles and synchronization resources. Ordinary/source buffers are not zeroed by the store; owned sensitive plaintext buffers are.
- `DocumentStoreException.Error` is `DocumentStoreError`: `InvalidRequest`, `Unavailable`, `UnsafePath`, `Limit`, `Conflict`, `Corrupt`, `UnsupportedVersion`, `Protection`, `Storage`, `Closed`. Fixed message only, no inner exception/path/payload. LocalDocumentStore checks write cancellation before admission. An admitted write retains its owned bytes and awaits the real CAS receipt, independently of caller cancellation or disposal. Reads remain cancellable. Cancellation/disposal cannot undo a won atomic replacement.

## Persistence and path safety

| Data | Directory relative to root | Committed file | On-disk bytes |
| --- | --- | --- | --- |
| Ordinary document | `n-<namespace>-<lowercase SHA256(namespace UTF-8)>` | `document.json` | Exact supplied bytes |
| Collection source | `c-<collection>-<lowercase SHA256(collection UTF-8)>` | `<import-content hash>.js` | Complete source bytes, at most 16 MiB |
| Sensitive document | `n-<lowercase SHA256(namespace UTF-8)>` | `document.mfdp` | Existing v1 DPAPI envelope |

The readable names identify ordinary data and collections. Namespace/collection identity digests avoid Windows case-insensitive filename collisions; generated prefixes/suffixes also keep reserved logical names such as `CON` safe. They do not authenticate plaintext content. Source reads do not compare the file's bytes or length with the import reference. Multiple source files may share one collection directory.

- Sensitive protection uses PInvoke `CryptProtectData` / `CryptUnprotectData`, **CurrentUser** (no `CRYPTPROTECT_LOCAL_MACHINE`), `CRYPTPROTECT_UI_FORBIDDEN`, no prompt/description. Native DPAPI output is freed with `LocalFree`.
- Sensitive optional entropy remains SHA-256 of the v1 domain marker, pinned product directory identity (volume serial plus file index) and exact namespace bytes. It is binding context, not a secret or second authentication factor. Ciphertext copied to a different root identity or namespace fails authentication. Account/device-session paths, entropy and bytes are unchanged.
- Sensitive outer format remains ASCII `MEOWDPAP` (8), version byte `01`, little-endian Int32 DPAPI-blob length, DPAPI blob. Inner authenticated payload: ASCII `MFDPDOC1` (8), little-endian Int32 data length, 32-byte content SHA-256, opaque bytes. Disk input is size-bounded before allocation (8 MiB + 64 KiB DPAPI allowance + 13-byte header).
- Sensitive unknown magic/version, malformed/trailing/truncated content, failed unprotect or inner length/hash are errors, **never an empty document**. Prior sensitive content is authenticated before replacement; a CAS write cannot replace corrupt/unreadable protected content. Plaintext documents have no such envelope or persisted integrity gate; JSON/business validity belongs to their consumer.
- Root validation rejects relative/UNC/device roots, network drives, alternate-data-stream syntax, traversal components, ambiguous trailing periods/spaces and reserved Windows path components. Every directory component is opened with `FILE_FLAG_OPEN_REPARSE_POINT`/backup semantics and checked through its handle. Pinned ancestor/root/data-directory handles include FILE_LIST_DIRECTORY and deny replacement while used. Ancestors must therefore be list-readable; denied access is an explicit failure, not a reparse/pinning bypass. Target files reject reparse points, directories, non-disk handles and multiple hardlinks before reading content, regardless of protection mode.
- An instance uses an explicit FIFO operation queue; a failed operation does not break the queue. Close rejects new admission, cancels reads and drains accepted writes before releasing directory handles. Cross-instance/cooperating-process serialization uses an exclusive zero-byte `.documents.lock` file in the pinned product root. Acquisition is bounded at five seconds (cross-process contention); read acquisition is cancellable, accepted write acquisition is not tied to its caller. First read can create this metadata lock, not a document/source file. Operations across namespaces serialize as well. This is **not CAS against writers bypassing this lock**, including a concurrently running external editor.
- Writes use a uniquely `CREATE_NEW` staging file in the same pinned destination directory. Ordinary/source stages contain raw bytes; sensitive stages contain ciphertext only. Win32 rename receives an explicitly NUL-terminated UTF-16 filename; declared byte length excludes the terminator. Directory pins allow read/write sharing but deny delete sharing: allowing `FILE_ADD_FILE` prevents conflict with our destination-directory pin. Flush to disk, then `SetFileInformationByHandle(FileRenameInfoEx)` with replace/Posix semantics on the already-open staging handle. Readers permitting delete sharing retain their old file; readers denying delete sharing and read-only targets block replacement. Unsupported systems/filesystems fail closed. No copy/delete fallback or fallible post-commit verification/cancellation that implies rollback.
- Failed pre-commit staging cleanup uses open-handle disposition, never a guessed path. Cleanup failure/crash can leave a stage. Ordinary/source reads and writes ignore neighboring unknown files/directories and interrupted stages; they never read or delete them automatically, and an absent target remains absent. Sensitive namespace inventory retains its strict rejection of unknown entries, including stages, with explicit recovery required. `Flush` + rename is not a tested power-loss durability guarantee.

## Reverse bridge API and assembly

```csharp
public ProtectedDocumentBridge(IDocumentStore store,
    IEnumerable<string> allowedNamespaces,
    ProtectedDocumentBridgeOptions? options = null,
    TimeProvider? timeProvider = null);

public IReadOnlyDictionary<string,
    Func<JsonElement, CancellationToken, Task<object?>>> Methods { get; }
public Guid Generation { get; }
public Task<ProtectedDocumentBridgeState> GetStateAsync();
public ValueTask DisposeAsync();
```

- Implements `IAsyncDisposable`. Anonymous lowercase-field DTOs are serialized by the parent's BridgePeer; no dependency on that type. Keep parameter `JsonElement` alive until its task finishes, as for other asynchronous peer methods.
- Product retains exactly one bridge per trusted control-session generation, registers its immutable method table once, closes/disposes the old bridge before constructing/rebinding a new peer, and never redirects old method invocations to the replacement bridge. `Generation` is native assembly identity, **not a new wire field**. An old captured table remains terminal after disposal; disposal does not dispose the injected store. Dispose bridges first, then their store owner.
- v1 has no wire generation field and no unlimited/cross-session replay authentication. It cannot infer a never-seen UUID's original generation. Parent transport/session generation gating remains mandatory. Raw peer `call` must not be handed to arbitrary scripts; this allowlist is not per-script authentication within one trusted peer.
- Store and bridge allowlists must be explicitly assembled (bridge may be narrower). No business name is compiled into platform storage. There is no namespace inventory or arbitrary file/key endpoint.
- `GetStateAsync` reports generation/closed state/counts/budget, not namespace, token or payload data. It is native diagnostics only; no wire method exports it.

## Exact document wire v1

`ProtectedDocumentBridge` implements these requests. Strict request objects reject extra/missing/duplicate fields. All include `version:1`, registered `namespace`, and canonical lowercase UUID `token`.

| Method suffix (`platform.documents.`) | Extra request fields | Extra success fields |
| --- | --- | --- |
| `read.begin` | none | `exists,length,sha256,chunkBytes:65536` |
| `read.chunk` | `offset` | `offset,next,data` |
| `write.begin` | `length,sha256,base:{exists,length,sha256}` | `offset:0` |
| `write.chunk` | `offset,data` | `offset,next` |
| `write.commit` | none | `length,sha256` |
| `release` | none | `released:true` |

Success fields additionally and only include `version:1,ok:true,token`; failure is exactly `{version:1,ok:false,code}`. Allowed codes: `INVALID_REQUEST`, `UNAVAILABLE`, `TOKEN`, `LIMIT`, `CLOSED`, `CANCELLED`, `TIMEOUT`, `CONFLICT`, `INTEGRITY`, `STORAGE`. No underlying exception is echoed.

- SHA-256 is lowercase 64-hex; absent fingerprint is exactly false/0/null. Offset/length are integral, nonnegative, at most 8 MiB. Every chunk is exactly `min(65536, length-offset)`, standard canonical padded base64, at most 87,384 encoded characters; reject whitespace, nonzero padding bits, short/long/repeated/out-of-order chunks.
- Read begin retains one immutable snapshot. Write begin validates current CAS base and reserves upload memory but does not persist. Commit consumes the upload token before awaiting the store, validates complete length/hash, then uses the store's atomic CAS. Wrong shape/chunk/integrity/cancellation discards only an unambiguously owned transfer; another namespace cannot evict it. Ambiguous duplicate context is rejected without guessing an owner.
- `release` is idempotent, allowed after close and with a cancelled per-call token. It handles lost begin ACKs by remembering even an otherwise unknown valid token. It never undoes a committed file.
- Commit ACK loss, timeout, cancel or teardown can occur **after persistence**. Never retry/replay a commit or roll back on that inference; re-read through a new operation/context and resolve business intent. The JS adapter reports commit uncertainty through `commitMayHaveSucceeded`.

## Limits, time and cleanup

- Defaults: four active transfers, 16 MiB reserved retained document bytes, 1,024 recent token digests, absolute 60-second transfer TTL. Hard bounds: eight transfers, 8–32 MiB retained budget, 4–4,096 recent digests (at least the transfer cap), 1–120 seconds TTL. Method calls have no separate pending-count quota; retained transfer buffers and wire-token rules are unchanged.
- Read begin pre-reserves 8 MiB before storage; on completion it shrinks to actual snapshot length. Write begin reserves declared length and allocates one final-size buffer (no repeated growing full-document copies). All invocations/store access are serialized. Limits count retained reservations, **not** total process RSS, caller/transport JSON, DPAPI native buffers or transient old-document authentication copies; no fabricated memory measurements.
- One rescheduled one-shot timer per bridge actively reclaims expired transfers; every call/state query also checks a monotonic clock. Store-call cancellation has the same absolute deadline. Chunks do not extend TTL. Cancellation cannot preempt a synchronous OS commit, and an injected provider retains its declared cancellation contract; LocalDocumentStore independently settles accepted writes.
- Retired tokens are stored only as SHA-256 digests with namespace ownership/deadline/timeout status for 2×TTL. Ledger cap never silently evicts old tokens. Overflow of unknown release sets one bounded begin-quarantine deadline (2×TTL after latest overflow) and returns `LIMIT`; no permanent service close or unbounded map. No new overflow permits recovery; continuing abusive overflow can extend quarantine.
- Close is terminal, cancels in-flight cooperative work, waits for serialized operations, clears owned buffers/history and disposes timer. The bridge retains best-effort zeroing of owned transport buffers; the store clears its owned sensitive plaintext buffers only. Neither promises erasure of GC, DPAPI, caller, serialized JSON/base64 or already-returned copies.
- **DPAPI is not screen-lock isolation and not protection against malicious same-user processes.** Product lock/background policy, credentials, process trust, backup/restore and real Windows behavior require separate explicit validation.

## Current format

Only the current data and wire formats are supported. Existing account/device-session DPAPI files retain their directory and envelope identities. Ordinary documents and v2 source collections use the new plaintext layout; old encrypted ordinary documents and v1 source chunks are not migrated, used as a fallback, or automatically deleted. This is a development format boundary, not a completed data migration. Corrupt/unsupported sensitive documents and inaccessible/unsafe target files are errors, not empty initial state. Cancellation cannot undo an acknowledged atomic commit.
