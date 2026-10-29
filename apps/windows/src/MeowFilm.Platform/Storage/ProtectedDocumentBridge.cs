using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Storage;

public sealed record ProtectedDocumentBridgeOptions
{
    public int MaximumTransfers { get; init; } = 4;
    public int MaximumBufferedBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumRecentTokens { get; init; } = 1024;
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaximumPendingCalls { get; init; } = 32;
}

public sealed record ProtectedDocumentBridgeState(Guid Generation, bool IsClosed, int Reads, int Writes,
    int ReservedBytes, int RecentTokens, int PendingCalls);

/// <summary>
/// Exact platform.documents v1 wire adapter. One instance per trusted peer generation;
/// no BridgePeer dependency, no business names, paths, keys or credential APIs on wire.
/// Dispose before replacing the peer. This service does not own/dispose its injected store.
/// </summary>
public sealed class ProtectedDocumentBridge : IAsyncDisposable
{
    public const int MaximumDocumentBytes = DocumentRules.MaximumDocumentBytes;
    public const int ChunkBytes = 64 * 1024;
    public Guid Generation { get; } = Guid.NewGuid();
    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> Methods { get; }

    private enum Kind { Read, Write }
    private enum Operation { ReadBegin, ReadChunk, WriteBegin, WriteChunk, Commit, Release }
    private sealed class Failure(string code) : Exception(code) { internal string Code { get; } = code; }
    private sealed class Transfer(string ns, Kind kind, long expires, int reservation)
    {
        internal readonly string Namespace = ns;
        internal readonly Kind Kind = kind;
        internal readonly long Expires = expires;
        internal int Reservation = reservation;
        internal byte[] Data = [];
        internal int Offset;
        internal DocumentFingerprint Fingerprint = DocumentFingerprint.Missing;
        internal DocumentFingerprint Base = DocumentFingerprint.Missing;
    }
    private sealed class Recent(string ns, long expires)
    {
        internal readonly string Namespace = ns;
        internal readonly long Expires = expires;
        internal bool TimedOut;
    }

    private readonly IProtectedDocumentStore store;
    private readonly HashSet<string> namespaces;
    private readonly ProtectedDocumentBridgeOptions options;
    private readonly TimeProvider time;
    private readonly long origin;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ITimer timer;
    private readonly Dictionary<string, Transfer> transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recent> recent = new(StringComparer.Ordinal);
    private long quarantineUntil;
    private int reserved, pending, stopping;

    public ProtectedDocumentBridge(IProtectedDocumentStore store, IEnumerable<string> allowedNamespaces,
        ProtectedDocumentBridgeOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        namespaces = DocumentRules.Namespaces(allowedNamespaces);
        this.options = options ?? new ProtectedDocumentBridgeOptions();
        Validate(this.options);
        time = timeProvider ?? TimeProvider.System;
        origin = time.GetTimestamp();
        timer = time.CreateTimer(static state => ((ProtectedDocumentBridge)state!).RequestReap(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var methods = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(StringComparer.Ordinal);
        foreach (var (name, operation) in new[]
        {
            ("read.begin", Operation.ReadBegin), ("read.chunk", Operation.ReadChunk),
            ("write.begin", Operation.WriteBegin), ("write.chunk", Operation.WriteChunk),
            ("write.commit", Operation.Commit), ("release", Operation.Release)
        }) methods.Add("platform.documents." + name, (value, cancellation) => InvokeAsync(operation, value, cancellation));
        Methods = new ReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(methods);
    }

    private long Now => time.GetElapsedTime(origin, time.GetTimestamp()).Ticks;
    private long Lifetime => options.TransferTimeout.Ticks;
    private bool Closed => Volatile.Read(ref stopping) != 0;
    private static string ReplayKey(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private static object Error(string code) => new { version = 1, ok = false, code };

    private async Task<object?> InvokeAsync(Operation operation, JsonElement value, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref pending) > options.MaximumPendingCalls)
        {
            Interlocked.Decrement(ref pending);
            return Error(Closed ? "CLOSED" : "LIMIT");
        }
        try
        {
            // Bounded admission; don't abandon a queued cancelled request before it
            // can discard its owned transfer. The store must honor cancellation pre-commit.
            await serial.WaitAsync().ConfigureAwait(false);
            (string Namespace, string Token)? context = null;
            try
            {
                Reap();
                context = OwnedContext(value);
                var fields = Object(value, Fields(operation));
                if (Integer(fields["version"], 1) != 1) throw new Failure("INVALID_REQUEST");
                string ns = String(fields["namespace"]), token = String(fields["token"]);
                if (!DocumentRules.ValidNamespace(ns) || !ValidToken(token)) throw new Failure("INVALID_REQUEST");
                if (!namespaces.Contains(ns)) throw new Failure("UNAVAILABLE");
                if (operation == Operation.Release)
                {
                    Release(ns, token);
                    return new { version = 1, ok = true, token, released = true };
                }
                if (Closed) throw new Failure("CLOSED");
                if (cancellation.IsCancellationRequested)
                {
                    Remember(ns, token);
                    throw new Failure("CANCELLED");
                }
                switch (operation)
                {
                    case Operation.ReadBegin:
                    {
                        var transfer = Begin(ns, token, Kind.Read, MaximumDocumentBytes);
                        var result = await StoreCall(ct => store.ReadAsync(ns, ct), transfer, cancellation).ConfigureAwait(false);
                        transfer.Data = result.Data ?? [];
                        transfer.Fingerprint = ValidateRead(result);
                        Check(transfer, cancellation);
                        reserved += transfer.Data.Length - transfer.Reservation;
                        transfer.Reservation = transfer.Data.Length;
                        var fingerprint = transfer.Fingerprint;
                        return new { version = 1, ok = true, token, exists = fingerprint.Exists,
                            length = fingerprint.Length, sha256 = fingerprint.Sha256, chunkBytes = ChunkBytes };
                    }
                    case Operation.WriteBegin:
                    {
                        int length = Integer(fields["length"], MaximumDocumentBytes);
                        string hash = Hash(fields["sha256"]);
                        var expected = ParseFingerprint(fields["base"]);
                        var transfer = Begin(ns, token, Kind.Write, length);
                        transfer.Fingerprint = new DocumentFingerprint(true, length, hash);
                        transfer.Base = expected;
                        var current = await StoreCall(ct => store.ReadAsync(ns, ct), transfer, cancellation).ConfigureAwait(false);
                        try
                        {
                            if (ValidateRead(current) != expected) throw new Failure("CONFLICT");
                        }
                        finally { if (current.Data is not null) CryptographicOperations.ZeroMemory(current.Data); }
                        Check(transfer, cancellation);
                        transfer.Data = new byte[length];
                        return new { version = 1, ok = true, token, offset = 0 };
                    }
                    case Operation.ReadChunk:
                    {
                        var transfer = Find(ns, token, Kind.Read);
                        int offset = Integer(fields["offset"], MaximumDocumentBytes);
                        if (offset != transfer.Offset || offset >= transfer.Data.Length) throw new Failure("INVALID_REQUEST");
                        int count = Math.Min(ChunkBytes, transfer.Data.Length - offset);
                        string data = Convert.ToBase64String(transfer.Data, offset, count);
                        transfer.Offset += count;
                        Check(transfer, cancellation);
                        return new { version = 1, ok = true, token, offset, next = transfer.Offset, data };
                    }
                    case Operation.WriteChunk:
                    {
                        var transfer = Find(ns, token, Kind.Write);
                        int offset = Integer(fields["offset"], MaximumDocumentBytes);
                        string encoded = String(fields["data"]);
                        if (offset != transfer.Offset || offset >= transfer.Data.Length || encoded.Length > 87384)
                            throw new Failure("INVALID_REQUEST");
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(encoded); }
                        catch (FormatException) { throw new Failure("INVALID_REQUEST"); }
                        try
                        {
                            if (bytes.Length != Math.Min(ChunkBytes, transfer.Data.Length - offset) ||
                                Convert.ToBase64String(bytes) != encoded) throw new Failure("INVALID_REQUEST");
                            bytes.CopyTo(transfer.Data, offset);
                            transfer.Offset += bytes.Length;
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                        Check(transfer, cancellation);
                        return new { version = 1, ok = true, token, offset, next = transfer.Offset };
                    }
                    case Operation.Commit:
                    {
                        var transfer = Find(ns, token, Kind.Write);
                        // Consume BEFORE awaiting the store. Neither lost ACK, cancellation,
                        // release nor a second commit can restore/replay this upload.
                        transfers.Remove(token);
                        reserved -= transfer.Reservation;
                        try
                        {
                            if (transfer.Offset != transfer.Data.Length ||
                                DocumentFingerprint.FromBytes(transfer.Data) != transfer.Fingerprint)
                                throw new Failure("INTEGRITY");
                            Check(transfer, cancellation);
                            var committed = await StoreCall(ct => store.WriteAsync(ns, transfer.Data, transfer.Base, ct),
                                transfer, cancellation).ConfigureAwait(false);
                            if (committed != transfer.Fingerprint) throw new Failure("STORAGE");
                            // No post-commit cancellation/close check: success may have won.
                            return new { version = 1, ok = true, token, length = committed.Length, sha256 = committed.Sha256 };
                        }
                        finally { CryptographicOperations.ZeroMemory(transfer.Data); }
                    }
                    default: throw new Failure("INVALID_REQUEST");
                }
            }
            catch (Exception exception)
            {
                string code = Code(exception);
                if (context is { } owner)
                {
                    if (code == "TIMEOUT" && recent.TryGetValue(ReplayKey(owner.Token), out var entry) && entry.Namespace == owner.Namespace)
                        entry.TimedOut = true;
                    Discard(owner.Namespace, owner.Token);
                }
                return Error(code);
            }
            finally
            {
                Schedule();
                serial.Release();
            }
        }
        finally { Interlocked.Decrement(ref pending); }
    }

    private async Task<T> StoreCall<T>(Func<CancellationToken, Task<T>> operation, Transfer transfer, CancellationToken cancellation)
    {
        Check(transfer, cancellation);
        long remaining = transfer.Expires - Now;
        if (remaining <= 0) throw new Failure("TIMEOUT");
        using var deadline = new CancellationTokenSource(TimeSpan.FromTicks(remaining), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token, deadline.Token);
        try { return await operation(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (Closed) throw new Failure("CLOSED");
            if (cancellation.IsCancellationRequested) throw new Failure("CANCELLED");
            if (deadline.IsCancellationRequested || Now >= transfer.Expires) throw new Failure("TIMEOUT");
            throw new Failure("CANCELLED");
        }
    }

    private static DocumentFingerprint ValidateRead(ProtectedDocument value)
    {
        if (value is null || value.Fingerprint is null) throw new Failure("STORAGE");
        if (value.Data?.Length > MaximumDocumentBytes) throw new Failure("LIMIT");
        var actual = value.Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(value.Data);
        if (!value.Fingerprint.IsValid || value.Fingerprint != actual) throw new Failure("STORAGE");
        return actual;
    }

    private void Check(Transfer transfer, CancellationToken cancellation)
    {
        if (Closed) throw new Failure("CLOSED");
        if (cancellation.IsCancellationRequested) throw new Failure("CANCELLED");
        if (Now >= transfer.Expires) throw new Failure("TIMEOUT");
    }

    private Transfer Begin(string ns, string token, Kind kind, int reservation)
    {
        string key = ReplayKey(token);
        if (recent.ContainsKey(key) || transfers.ContainsKey(token)) throw new Failure("TOKEN");
        if (quarantineUntil > Now || recent.Count >= options.MaximumRecentTokens || transfers.Count >= options.MaximumTransfers ||
            reserved + reservation > options.MaximumBufferedBytes) throw new Failure("LIMIT");
        long now = Now;
        recent.Add(key, new Recent(ns, checked(now + Lifetime * 2)));
        var transfer = new Transfer(ns, kind, checked(now + Lifetime), reservation);
        transfers.Add(token, transfer);
        reserved += reservation;
        return transfer;
    }

    private Transfer Find(string ns, string token, Kind kind)
    {
        if (transfers.TryGetValue(token, out var transfer) && transfer.Namespace == ns && transfer.Kind == kind) return transfer;
        if (recent.TryGetValue(ReplayKey(token), out var old) && old.Namespace == ns && old.TimedOut) throw new Failure("TIMEOUT");
        throw new Failure("TOKEN");
    }

    private void Discard(string ns, string token)
    {
        if (!transfers.TryGetValue(token, out var transfer) || transfer.Namespace != ns) return;
        transfers.Remove(token);
        reserved -= transfer.Reservation;
        CryptographicOperations.ZeroMemory(transfer.Data);
    }

    private void Release(string ns, string token)
    {
        if (recent.TryGetValue(ReplayKey(token), out var owner) && owner.Namespace != ns) throw new Failure("TOKEN");
        Discard(ns, token);
        if (!Closed && !Remember(ns, token)) throw new Failure("LIMIT");
    }

    private bool Remember(string ns, string token)
    {
        string key = ReplayKey(token);
        if (recent.ContainsKey(key)) return true;
        long expires = checked(Now + Lifetime * 2);
        if (recent.Count >= options.MaximumRecentTokens)
        {
            // Don't evict prior rejection history, silently forget a release, or grow
            // forever. New begins are quarantined for at most 2 TTL after last overflow.
            quarantineUntil = Math.Max(quarantineUntil, expires);
            return false;
        }
        recent.Add(key, new Recent(ns, expires));
        return true;
    }

    private void Reap()
    {
        long now = Now;
        foreach (var pair in transfers.ToArray())
        {
            if (pair.Value.Expires > now) continue;
            Discard(pair.Value.Namespace, pair.Key);
            if (recent.TryGetValue(ReplayKey(pair.Key), out var entry)) entry.TimedOut = true;
        }
        foreach (var pair in recent.ToArray()) if (pair.Value.Expires <= now) recent.Remove(pair.Key);
        if (quarantineUntil <= now) quarantineUntil = 0;
    }

    private void Schedule()
    {
        if (Closed) return;
        long? next = transfers.Count == 0 ? null : transfers.Values.Min(value => value.Expires);
        timer.Change(next is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(Math.Max(1, next.Value - Now)),
            Timeout.InfiniteTimeSpan);
    }

    private void RequestReap() => _ = ReapTimerAsync();
    private async Task ReapTimerAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try { if (!Closed) { Reap(); Schedule(); } }
        finally { serial.Release(); }
    }

    public async Task<ProtectedDocumentBridgeState> GetStateAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            Reap();
            return new(Generation, Closed, transfers.Values.Count(t => t.Kind == Kind.Read),
                transfers.Values.Count(t => t.Kind == Kind.Write), reserved, recent.Count, Volatile.Read(ref pending));
        }
        finally { serial.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0) lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var transfer in transfers.Values) CryptographicOperations.ZeroMemory(transfer.Data);
            transfers.Clear();
            recent.Clear();
            reserved = 0;
            quarantineUntil = 0;
        }
        finally { serial.Release(); }
        await timer.DisposeAsync().ConfigureAwait(false);
    }

    private (string Namespace, string Token)? OwnedContext(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string? ns = null, token = null;
        int nsCount = 0, tokenCount = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("namespace")) { nsCount++; if (property.Value.ValueKind == JsonValueKind.String) ns = property.Value.GetString(); }
            if (property.NameEquals("token")) { tokenCount++; if (property.Value.ValueKind == JsonValueKind.String) token = property.Value.GetString(); }
        }
        return nsCount == 1 && tokenCount == 1 && ns is not null && token is not null && namespaces.Contains(ns) && ValidToken(token)
            ? (ns, token) : null;
    }

    private static string[] Fields(Operation operation) => operation switch
    {
        Operation.ReadChunk => ["version", "namespace", "token", "offset"],
        Operation.WriteBegin => ["version", "namespace", "token", "length", "sha256", "base"],
        Operation.WriteChunk => ["version", "namespace", "token", "offset", "data"],
        _ => ["version", "namespace", "token"]
    };

    private static Dictionary<string, JsonElement> Object(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new Failure("INVALID_REQUEST");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value))
                throw new Failure("INVALID_REQUEST");
        if (result.Count != fields.Length) throw new Failure("INVALID_REQUEST");
        return result;
    }

    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new Failure("INVALID_REQUEST");
    private static string Hash(JsonElement value)
    {
        string hash = String(value);
        return DocumentRules.ValidHash(hash) ? hash : throw new Failure("INVALID_REQUEST");
    }
    private static int Integer(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
            !double.IsFinite(number) || number < 0 || number > maximum || Math.Truncate(number) != number)
            throw new Failure("INVALID_REQUEST");
        return (int)number;
    }
    private static DocumentFingerprint ParseFingerprint(JsonElement value)
    {
        var fields = Object(value, ["exists", "length", "sha256"]);
        if (fields["exists"].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new Failure("INVALID_REQUEST");
        int length = Integer(fields["length"], MaximumDocumentBytes);
        if (!fields["exists"].GetBoolean())
        {
            if (length != 0 || fields["sha256"].ValueKind != JsonValueKind.Null) throw new Failure("INVALID_REQUEST");
            return DocumentFingerprint.Missing;
        }
        return new DocumentFingerprint(true, length, Hash(fields["sha256"]));
    }
    private static bool ValidToken(string token) => token.Length == 36 && Guid.TryParseExact(token, "D", out var parsed) &&
        parsed.ToString("D") == token;

    private string Code(Exception exception) => exception switch
    {
        Failure failure => failure.Code,
        ProtectedDocumentException { Error: DocumentStoreError.Limit } => "LIMIT",
        ProtectedDocumentException { Error: DocumentStoreError.Conflict } => "CONFLICT",
        ProtectedDocumentException { Error: DocumentStoreError.Closed } => "CLOSED",
        ProtectedDocumentException { Error: DocumentStoreError.Unavailable } => "UNAVAILABLE",
        OperationCanceledException => Closed ? "CLOSED" : "CANCELLED",
        _ => "STORAGE"
    };

    private static void Validate(ProtectedDocumentBridgeOptions options)
    {
        if (options.MaximumTransfers is < 1 or > 8 || options.MaximumBufferedBytes < MaximumDocumentBytes ||
            options.MaximumBufferedBytes > 32 * 1024 * 1024 || options.MaximumRecentTokens < Math.Max(4, options.MaximumTransfers) ||
            options.MaximumRecentTokens > 4096 || options.TransferTimeout < TimeSpan.FromSeconds(1) ||
            options.TransferTimeout > TimeSpan.FromSeconds(120) || options.MaximumPendingCalls is < 1 or > 64)
            throw new ArgumentException("Invalid document bridge configuration.");
    }
}
