using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

public sealed record MusicFeedbackSettings(long Revision, IReadOnlyList<string> Enabled);

/// <summary>One native-only preferences owner. It never stores listening instances or credentials.</summary>
public sealed class MusicFeedbackSettingsOwner(IDocumentStore store) : IAsyncDisposable
{
    public const string DocumentNamespace = "music.feedback.v2";
    public const string ContractVersion = "1.0.0";
    private const long MaximumRevision = 9007199254740991;
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly object sync = new();
    private MusicFeedbackSettings current = new(0, Array.Empty<string>());
    private JsonObject document = new();
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private bool loaded, uncertain, closed, preservesWyConsent;
    public event Action? Changed;

    public bool IsEnabled(string platform)
    { lock (sync) return !closed && loaded && !uncertain && current.Enabled.Contains(platform); }
    public bool IsConfirmed { get { lock (sync) return !closed && loaded && !uncertain; } }
    public bool RequiresReconciliation { get { lock (sync) return loaded && uncertain; } }
    internal long ConfirmedRevision
    { get { lock (sync) { if (closed || !loaded || uncertain) throw Error("COMMIT_UNKNOWN"); return current.Revision; } } }
    // An in-process CAS that keeps WY enabled can suspend its network without
    // revoking its listening facts. This is not persisted proof across a crash.
    internal bool PreservesWyConsent { get { lock (sync) return !closed && preservesWyConsent; } }
    internal long WyConsentRevision
    {
        get
        {
            lock (sync)
            {
                if (closed || !loaded || !current.Enabled.Contains("wy") || uncertain && !preservesWyConsent)
                    throw Error("COMMIT_UNKNOWN");
                return current.Revision;
            }
        }
    }

    public async Task<MusicFeedbackSettings> ReadAsync(bool refresh = false, CancellationToken token = default)
    {
        await lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                if (loaded && !refresh)
                {
                    if (uncertain) throw Error("COMMIT_UNKNOWN");
                    return current;
                }
            }
            var saved = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
            var decoded = Decode(saved, out var raw);
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                preservesWyConsent = loaded && !uncertain && current.Revision == decoded.Revision &&
                    current.Enabled.Contains("wy") && decoded.Enabled.Contains("wy");
                current = decoded; document = raw; fingerprint = saved.Fingerprint; loaded = true; uncertain = false;
            }
            Notify(); return decoded;
        }
        catch
        {
            lock (sync) { uncertain = true; preservesWyConsent = false; }
            Notify(); throw;
        }
        finally { lane.Release(); }
    }

    public async Task<MusicFeedbackSettings> SaveAsync(long expectedRevision, IReadOnlyList<string> enabled,
        CancellationToken token = default)
    {
        string[] targets = enabled?.ToArray() ?? throw Error("INVALID_REQUEST");
        if (expectedRevision is < 0 or >= MaximumRevision || targets.Length > 3 ||
            targets.Distinct(StringComparer.Ordinal).Count() != targets.Length ||
            targets.Any(value => value is not ("wy" or "tx" or "kg"))) throw Error("INVALID_REQUEST");
        Array.Sort(targets, StringComparer.Ordinal);
        await ReadAsync(token: token).ConfigureAwait(false);
        await lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            MusicFeedbackSettings next;
            JsonObject nextDocument;
            byte[] data;
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                if (uncertain) throw Error("COMMIT_UNKNOWN");
                if (current.Revision != expectedRevision) throw Error("REVISION_CONFLICT");
                token.ThrowIfCancellationRequested();
                next = new(expectedRevision + 1, new ReadOnlyCollection<string>(targets));
                nextDocument = document.DeepClone().AsObject();
                nextDocument["version"] = 1; nextDocument["revision"] = next.Revision;
                nextDocument["enabled"] = JsonSerializer.SerializeToNode(targets);
                data = JsonSerializer.SerializeToUtf8Bytes(nextDocument);
                if (data.Length > 1024) throw Error("LIMIT");
                preservesWyConsent = current.Enabled.Contains("wy") && targets.Contains("wy");
                // Fence all reporting BEFORE admitting a settings mutation. A lost
                // acknowledgment must not leave the old enabled policy operational.
                uncertain = true;
            }
            Notify();
            try
            {
                var expected = DocumentFingerprint.FromBytes(data);
                // A final CAS admission cannot be undone by UI cancellation.
                var actual = await store.WriteAsync(DocumentNamespace, data, fingerprint, CancellationToken.None).ConfigureAwait(false);
                if (actual != expected) throw Error("COMMIT_UNKNOWN");
                lock (sync) { current = next; document = nextDocument; fingerprint = actual; uncertain = false; }
                Notify(); return next;
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict)
            {
                lock (sync) preservesWyConsent = false;
                Notify(); throw Error("STORAGE_CONFLICT");
            }
            catch
            {
                lock (sync) preservesWyConsent = false;
                Notify(); throw Error("COMMIT_UNKNOWN");
            }
        }
        finally { lane.Release(); }
    }

    private static MusicFeedbackSettings Decode(DocumentSnapshot saved, out JsonObject raw)
    {
        if (saved.Data is null)
        {
            if (saved.Fingerprint != DocumentFingerprint.Missing) throw Error("CORRUPT_STORAGE");
            raw = new(); return new(0, Array.Empty<string>());
        }
        try
        {
            if (saved.Data.Length > 1024)
                throw Error("CORRUPT_STORAGE");
            using var doc = JsonDocument.Parse(saved.Data, new() { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Error("CORRUPT_STORAGE");
            var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
                root.GetProperty("enabled").ValueKind != JsonValueKind.Array) throw Error("CORRUPT_STORAGE");
            if (Integer(root.GetProperty("version"), 1) != 1)
                throw Error("CORRUPT_STORAGE");
            long revision = Integer(root.GetProperty("revision"), MaximumRevision);
            var enabled = root.GetProperty("enabled").EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } platform)
                    throw Error("CORRUPT_STORAGE");
                return platform;
            }).ToArray();
            if (enabled.Length > 3 || enabled.Distinct(StringComparer.Ordinal).Count() != enabled.Length ||
                enabled.Any(v => v is not ("wy" or "tx" or "kg")))
                throw Error("CORRUPT_STORAGE");
            Array.Sort(enabled, StringComparer.Ordinal);
            raw = JsonNode.Parse(saved.Data)!.AsObject();
            return new(revision, new ReadOnlyCollection<string>(enabled));
        }
        catch (MusicFeedbackException) { throw; }
        catch { throw Error("CORRUPT_STORAGE"); }
    }

    private static long Integer(JsonElement value, long maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal result) ||
            result < 0 || result > maximum || decimal.Truncate(result) != result)
            throw Error("CORRUPT_STORAGE");
        return (long)result;
    }

    private void Notify() => Changed?.Invoke();
    private static MusicFeedbackException Error(string code) => new(code);
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (closed) return; closed = true; }
        Notify();
        await lane.WaitAsync().ConfigureAwait(false);
        lane.Release(); // Borrowed store is never disposed here.
    }
}
