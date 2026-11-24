using System.Text.Json;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private CancellationTokenSource? chorusCancellation;
    private string? chorusKey;
    private double? chorusStart, chorusEnd;
    private long chorusAccountEpoch, chorusIssuedEpoch;

    public bool HasChorus => chorusStart is { } start && chorusEnd is { } end &&
        chorusIssuedEpoch == Interlocked.Read(ref chorusAccountEpoch) && OwnsCurrent && playback.HasMedia &&
        start < DurationSeconds && end <= DurationSeconds;
    public string ChorusLabel => HasChorus ? "官方高潮 " + MusicPlaybackClock.FormatClock(chorusStart!.Value) : "";
    public double ChorusPositionSeconds => HasChorus ? chorusStart!.Value : double.NaN;

    private void ChorusBindings()
    {
        Changed(nameof(HasChorus)); Changed(nameof(ChorusLabel));
        Changed(nameof(ChorusPositionSeconds));
    }

    private void ClearChorus()
    {
        var cancellation = chorusCancellation;
        chorusCancellation = null;
        cancellation?.Cancel();
        bool hadState = chorusKey is not null || chorusStart is not null || chorusEnd is not null;
        chorusKey = null; chorusStart = chorusEnd = null;
        if (hadState) ChorusBindings();
    }

    private void ChorusAccountChanged()
    {
        Interlocked.Increment(ref chorusAccountEpoch); // Fence before queued UI work.
        if (!ctx.Dispatcher.IsClosing)
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (stopping || disposed) return;
                ClearChorus();
                SyncChorus();
            });
    }

    private void SyncChorus()
    {
        // No title matching or borrowing another edition/platform when the selected source lacks metadata.
        if (stopping || !OwnsCurrent || !playback.HasMedia || current is null ||
            musicRecovery?.Media?.Target is not { SourceKey: not null } target)
        { if (chorusKey is not null) ClearChorus(); return; }
        var rows = current.Record.Sources.Where(row => row.SourceKey == target.SourceKey).ToArray();
        if (rows.Length != 1) { ClearChorus(); return; }
        var source = rows[0];
        string platform = target.Platform;
        string id = platform switch { "wy" or "tx" => source.SongMid ?? "", "kg" => source.Hash ?? "", _ => "" };
        if (id.Length == 0) { ClearChorus(); return; }
        if (source.Duration is not { } expected || !double.IsFinite(expected) || expected <= 0 || Math.Abs(expected - DurationSeconds) > 10)
        { if (chorusKey is not null) ClearChorus(); return; }
        var epoch = Interlocked.Read(ref chorusAccountEpoch);
        var key = PlaybackPresentationKey + "\0" + target.SourceKey + "\0" + epoch;
        var durableKey = new MusicChorusKey(target.AccountScope ?? "script:" + target.Installation,
            platform, id, expected);
        if (key == chorusKey) { ChorusBindings(); return; }
        ClearChorus(); chorusKey = key; chorusIssuedEpoch = epoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        chorusCancellation = request;
        var work = Read(); AddEventWrite(work); _ = Observe();
        async Task Read()
        {
            try
            {
                var cached = await persistentChorus.ReadAsync(durableKey, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                if (cached is { } hit && hit.End <= DurationSeconds)
                { chorusStart = hit.Start; chorusEnd = hit.End; ChorusBindings(); return; }
                var reply = await application.GetCatalogChorusAsync(platform, id, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                var point = reply.GetProperty("point");
                if (point.ValueKind != JsonValueKind.Object) return;
                double start = point.GetProperty("start").GetDouble(), end = point.GetProperty("end").GetDouble();
                if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start &&
                    end <= expected && end <= DurationSeconds)
                {
                    chorusStart = start; chorusEnd = end; ChorusBindings();
                    await persistentChorus.PutAsync(durableKey, start, end, request.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "chorus", "read-failed",
                    new { platform, error = error.Message });
            }
            finally { if (ReferenceEquals(chorusCancellation, request)) chorusCancellation = null; request.Dispose(); }
        }
        async Task Observe() { try { await work; } finally { RemoveEventWrite(work); } }
    }
}
