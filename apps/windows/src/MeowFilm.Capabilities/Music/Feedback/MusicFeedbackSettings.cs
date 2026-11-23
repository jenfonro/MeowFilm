using System.Collections.ObjectModel;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

public sealed record MusicFeedbackSettings(long Revision, IReadOnlyList<string> Enabled);

/// <summary>One native-only preferences owner. It never stores listening instances or credentials.</summary>
public sealed class MusicFeedbackSettingsOwner(IStorageDatabase database) : IAsyncDisposable
{
    public const string StorageOwner = "music.feedback.v1";
    public const string ContractVersion = "1.0.0";
    private const long MaximumRevision = 9007199254740991;
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly object sync = new();
    private MusicFeedbackSettings current = new(0, Array.Empty<string>());
    private StorageRevision storageRevision = StorageRevision.Empty;
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
            MusicFeedbackSettings decoded;
            StorageRevision savedRevision;
            try
            {
                (decoded, savedRevision) = await MusicFeedbackSettingsStore.ReadAsync(database, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                bool notify;
                lock (sync)
                {
                    notify = !closed;
                    if (notify) { uncertain = true; preservesWyConsent = false; }
                }
                if (notify) Notify();
                throw;
            }
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                preservesWyConsent = loaded && !uncertain && current.Revision == decoded.Revision &&
                    current.Enabled.Contains("wy") && decoded.Enabled.Contains("wy");
                current = decoded; storageRevision = savedRevision; loaded = true; uncertain = false;
            }
            Notify(); return decoded;
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
            StorageRevision expectedStorageRevision;
            lock (sync)
            {
                if (closed) throw Error("CLOSED");
                if (uncertain) throw Error("COMMIT_UNKNOWN");
                if (current.Revision != expectedRevision) throw Error("REVISION_CONFLICT");
                token.ThrowIfCancellationRequested();
                next = new(expectedRevision + 1, new ReadOnlyCollection<string>(targets));
                expectedStorageRevision = storageRevision;
                preservesWyConsent = current.Enabled.Contains("wy") && targets.Contains("wy");
                // Fence all reporting BEFORE admitting a settings mutation. A lost
                // acknowledgment must not leave the old enabled policy operational.
                uncertain = true;
            }
            Notify();
            try
            {
                // A final CAS admission cannot be undone by UI cancellation.
                var actual = await MusicFeedbackSettingsStore.SaveAsync(database, next, expectedStorageRevision, CancellationToken.None).ConfigureAwait(false);
                lock (sync) { current = next; storageRevision = actual; uncertain = false; }
                Notify(); return next;
            }
            catch (StorageException error) when (error.Error == StorageError.Conflict)
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
