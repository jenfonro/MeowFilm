using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

public sealed record MusicFeedbackSettings(long Revision, IReadOnlyList<string> Enabled);

/// <summary>One native-only preferences owner. It never stores listening instances or credentials.</summary>
public sealed class MusicFeedbackSettingsOwner(IDocumentStore store) : IAsyncDisposable
{
    public const string DocumentNamespace = "music.feedback.v1";
    public const string ContractVersion = "1.0.0";
    private const long MaximumRevision = 9007199254740991;
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly object sync = new();
    private MusicFeedbackSettings current = new(0, Array.Empty<string>());
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private bool loaded, uncertain, closed;
    public event Action? Changed;

    public bool IsEnabled(string platform)
    { lock (sync) return !closed && loaded && !uncertain && current.Enabled.Contains(platform); }
    public bool IsConfirmed { get { lock (sync) return !closed && loaded && !uncertain; } }
    public bool RequiresReconciliation { get { lock (sync) return loaded && uncertain; } }

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
            var decoded = Decode(saved);
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                current = decoded; fingerprint = saved.Fingerprint; loaded = true; uncertain = false;
            }
            Notify(); return decoded;
        }
        catch
        {
            lock (sync) uncertain = true;
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
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                if (uncertain) throw Error("COMMIT_UNKNOWN");
                if (current.Revision != expectedRevision) throw Error("REVISION_CONFLICT");
                token.ThrowIfCancellationRequested();
                next = new(expectedRevision + 1, new ReadOnlyCollection<string>(targets));
                // Fence all reporting BEFORE admitting a settings mutation. A lost
                // acknowledgment must not leave the old enabled policy operational.
                uncertain = true;
            }
            Notify();
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, revision = next.Revision, enabled = targets });
            try
            {
                var expected = DocumentFingerprint.FromBytes(data);
                // A final CAS admission cannot be undone by UI cancellation.
                var actual = await store.WriteAsync(DocumentNamespace, data, fingerprint, CancellationToken.None).ConfigureAwait(false);
                if (actual != expected) throw Error("COMMIT_UNKNOWN");
                lock (sync) { current = next; fingerprint = actual; uncertain = false; }
                Notify(); return next;
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict)
            { throw Error("STORAGE_CONFLICT"); }
            catch { throw Error("COMMIT_UNKNOWN"); }
        }
        finally { lane.Release(); }
    }

    private static MusicFeedbackSettings Decode(DocumentSnapshot saved)
    {
        if (saved.Data is null)
        {
            if (saved.Fingerprint != DocumentFingerprint.Missing) throw Error("CORRUPT_STORAGE");
            return new(0, Array.Empty<string>());
        }
        try
        {
            if (saved.Data.Length > 1024)
                throw Error("CORRUPT_STORAGE");
            using var doc = JsonDocument.Parse(saved.Data, new() { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Error("CORRUPT_STORAGE");
            var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (keys.Length != 3 || keys.Distinct(StringComparer.Ordinal).Count() != 3 ||
                keys.Any(k => k is not ("version" or "revision" or "enabled")) ||
                root.GetProperty("enabled").ValueKind != JsonValueKind.Array) throw Error("CORRUPT_STORAGE");
            if (CanonicalInteger(root.GetProperty("version"), 1) != 1)
                throw Error("CORRUPT_STORAGE");
            long revision = CanonicalInteger(root.GetProperty("revision"), MaximumRevision);
            var enabled = root.GetProperty("enabled").EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } platform)
                    throw Error("CORRUPT_STORAGE");
                return platform;
            }).ToArray();
            if (enabled.Length > 3 || enabled.Distinct(StringComparer.Ordinal).Count() != enabled.Length ||
                enabled.Any(v => v is not ("wy" or "tx" or "kg")) ||
                !enabled.SequenceEqual(enabled.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
                throw Error("CORRUPT_STORAGE");
            return new(revision, new ReadOnlyCollection<string>(enabled));
        }
        catch (MusicFeedbackException) { throw; }
        catch { throw Error("CORRUPT_STORAGE"); }
    }

    private static long CanonicalInteger(JsonElement value, long maximum)
    {
        string raw = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
        if (raw.Length is < 1 or > 16 || raw.Length > 1 && raw[0] == '0' ||
            raw.Any(character => character is < '0' or > '9') ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long result) ||
            result > maximum)
            throw Error("CORRUPT_STORAGE");
        return result;
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
