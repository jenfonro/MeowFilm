# Windows protected documents — v1

The App uses the `aggregate` data directory and the namespaces declared by `AppIdentity.NativeNamespaces`.

Native owners use the store directly. The optional script process has no ordinary business-document namespace grant; bridge access is limited by its explicit namespace grants.

## Public store API (MeowFilm.Platform.Storage)

```csharp
public sealed record DocumentFingerprint(bool Exists, int Length, string? Sha256)
{
    public static DocumentFingerprint Missing { get; }
    public static DocumentFingerprint FromBytes(ReadOnlySpan<byte> bytes);
}
public sealed record ProtectedDocument(byte[]? Data, DocumentFingerprint Fingerprint);

public interface IProtectedDocumentStore : IAsyncDisposable
{
    Task<ProtectedDocument> ReadAsync(string documentNamespace,
        CancellationToken cancellationToken = default);
    Task<DocumentFingerprint> WriteAsync(string documentNamespace,
        ReadOnlyMemory<byte> data, DocumentFingerprint expected,
        CancellationToken cancellationToken = default);
}

public DpapiDocumentStore(string productRoot, IEnumerable<string> allowedNamespaces);
```

- One opaque business document per namespace, at most 8 MiB. The store does not decode/normalize JSON. Native reading data or local-file grant metadata may use separate, explicitly configured business namespaces; it does not open the granted files or implement an account protocol.
- Root must already be a dedicated, app-owned directory on a local fixed/removable volume with a nonzero directory file identity. Constructor validates/pins it without creating directories/files or calling DPAPI. Product owns root selection, ACL policy and lifetime; the App continues using its existing aggregate root. Do not supply a shared/world-writable or roaming/cloud-synchronized root.
- Namespaces are a fixed allowlist (0–64 unique entries), case-sensitive. Syntax: 1–128 ASCII characters, first alphanumeric, remainder alphanumeric/period/underscore/hyphen, no `..`. Names such as `CON` are safe logical identities because they never become direct filenames. Invalid names fail before any storage access; valid unregistered names return `Unavailable`.
- `Data == null` means genuinely absent. An existing zero-byte document has `Exists=true`, `Length=0`, the empty-content SHA-256, and a non-null empty array. Caller owns returned bytes.
- Every write needs an exact base fingerprint (`Missing` only for an absent document). The store compares and writes while holding the same root-wide lock. Fingerprints are plaintext existence/length/SHA-256, **not monotonic revisions**; identical-content A→B→A cannot be distinguished.
- Write takes an owned byte snapshot before its first asynchronous suspension, after bounded admission. Maximum eight pending native operations per store; excess returns `Limit`. One operation executes at a time and blocking native/filesystem work runs off the caller's UI thread.
- `ProtectedDocumentException.Error` is `DocumentStoreError`: `InvalidRequest`, `Unavailable`, `UnsafePath`, `Limit`, `Conflict`, `Corrupt`, `UnsupportedVersion`, `Protection`, `Storage`, `Closed`. Fixed message only, no inner exception/path/payload. Cancellation before the commit point raises cancellation; cancellation/disposal cannot undo a won atomic replacement.

## Persistence and path safety

- PInvoke only: `CryptProtectData` / `CryptUnprotectData`, **CurrentUser** (no `CRYPTPROTECT_LOCAL_MACHINE`), `CRYPTPROTECT_UI_FORBIDDEN`, no prompt/description. Native DPAPI output is freed with `LocalFree`.
- Namespace directory: `n-<lowercase SHA256(namespace UTF-8)>`; committed file: `document.mfdp`. Logical case-sensitive namespace identity survives case-insensitive filesystems.
- DPAPI optional entropy is SHA-256 of a v1 domain marker, pinned product directory identity (volume serial plus file index) and the exact namespace bytes. It is binding context, not a secret or a second authentication factor. Ciphertext copied to a different root identity or namespace fails authentication.
- Outer format: ASCII `MEOWDPAP` (8), version byte `01`, little-endian Int32 DPAPI-blob length, DPAPI blob. Inner authenticated payload: ASCII `MFDPDOC1` (8), little-endian Int32 data length, 32-byte content SHA-256, opaque bytes. Disk input is size-bounded before allocation (8 MiB + 64 KiB DPAPI allowance + 13-byte header).
- Unknown magic/version, malformed/trailing/truncated content, failed unprotect, failed inner length/hash, inaccessible or unsafe paths are errors, **never an empty document**. Prior content is authenticated before replacement; a CAS write cannot replace corrupt/unreadable content.
- Root validation rejects relative/UNC/device roots, network drives, alternate-data-stream syntax, traversal components, ambiguous trailing periods/spaces and reserved Windows path components. Every directory component is opened with `FILE_FLAG_OPEN_REPARSE_POINT`/backup semantics and checked through its handle. Pinned ancestor/root/namespace handles include FILE_LIST_DIRECTORY (attribute-only access is insufficient for sharing enforcement) and deny replacement while used. Ancestors must therefore be list-readable; denied access is an explicit failure, not a reparse/pinning bypass. Files reject reparse points, directories, non-disk handles and multiple hardlinks before reading content.
- Cross-instance/cooperating-process serialization uses an exclusive zero-byte `.documents.lock` file in the pinned product root. Acquisition is cancellable and bounded at five seconds. First read can create this metadata lock, not a document or plaintext file. Native operations across namespaces serialize as well. This is **not CAS against writers bypassing this lock**.
- Only ciphertext is written to a uniquely `CREATE_NEW` staging file in the same pinned namespace directory. Win32 rename receives an explicitly NUL-terminated UTF-16 filename; its declared byte length excludes the terminator. Directory pins allow read/write sharing but deny delete sharing: allowing `FILE_ADD_FILE` prevents conflict with our own destination-directory pin. Flush ciphertext to disk, check cancellation, then `SetFileInformationByHandle(FileRenameInfoEx)` with replace/Posix semantics on the already-open staging handle. Readers permitting delete sharing retain their old file; readers denying delete sharing and read-only targets still block replacement. Unsupported systems/filesystems fail closed. No copy/delete fallback, and no fallible post-commit verification or cancellation check that implies rollback.
- Failed pre-commit staging cleanup is by open-handle disposition, never a guessed path. A cleanup failure/crash can leave a ciphertext stage. Unknown files/directories, including such stages, fail closed and are not silently read, deleted or treated as a fresh namespace; recovery is explicit. `Flush` + rename is not a tested power-loss durability guarantee.

## Reverse bridge API and assembly

```csharp
public ProtectedDocumentBridge(IProtectedDocumentStore store,
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

- Defaults: four active transfers, 16 MiB reserved retained document bytes, 1,024 recent token digests, absolute 60-second transfer TTL, 32 pending method calls. Hard bounds: eight transfers, 8–32 MiB retained budget, 4–4,096 recent digests (at least the transfer cap), 1–120 seconds TTL, 1–64 pending calls.
- Read begin pre-reserves 8 MiB before storage; on completion it shrinks to actual snapshot length. Write begin reserves declared length and allocates one final-size buffer (no repeated growing full-document copies). All invocations/store access are serialized. Limits count retained reservations, **not** total process RSS, caller/transport JSON, DPAPI native buffers or transient old-document authentication copies; no fabricated memory measurements.
- One rescheduled one-shot timer per bridge actively reclaims expired transfers; every call/state query also checks a monotonic clock. Store-call cancellation has the same absolute deadline. Chunks do not extend TTL. Cancellation cannot preempt a synchronous OS commit, and an injected provider must honor cancellation before its commit point.
- Retired tokens are stored only as SHA-256 digests with namespace ownership/deadline/timeout status for 2×TTL. Ledger cap never silently evicts old tokens. Overflow of unknown release sets one bounded begin-quarantine deadline (2×TTL after latest overflow) and returns `LIMIT`; no permanent service close or unbounded map. No new overflow permits recovery; continuing abusive overflow can extend quarantine.
- Close is terminal, cancels in-flight cooperative work, waits for serialized operations, clears owned buffers/history and disposes timer. Bridge/store use best-effort zeroing of owned byte buffers, but do not promise erasure of GC, DPAPI, caller, serialized JSON/base64 or already-returned copies.
- **DPAPI is not screen-lock isolation and not protection against malicious same-user processes.** Product lock/background policy, credentials, process trust, backup/restore and real Windows behavior require separate explicit validation.

## Current format

Only the current data and wire formats are supported. Corrupt, unknown, or unsupported documents are not empty initial state; there is no automatic plaintext fallback, relocation, conversion, rollback, or destructive repair. Cancellation cannot undo an acknowledged atomic commit.
