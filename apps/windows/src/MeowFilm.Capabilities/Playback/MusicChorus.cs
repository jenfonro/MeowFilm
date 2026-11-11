using System.Text.Json;
using System.Windows;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities;

public interface IMusicChorusInvalidationSource
{
    event Action? ChorusInvalidated;
}

public sealed partial class MusicNativeApplication
{
    public event Action? ChorusInvalidated;
    private void InvalidateChorus(string platform)
    {
        foreach (var handler in ChorusInvalidated?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { /* Display cannot block credential retirement. */ }
    }
}

public sealed partial class MusicCapability
{
    private CancellationTokenSource? chorusCancellation;
    private string? chorusKey;
    private double? chorusStart, chorusEnd;
    private long chorusAccountEpoch, chorusIssuedEpoch;
    public bool HasChorus => chorusStart is { } start && chorusEnd is { } end &&
        chorusIssuedEpoch == Interlocked.Read(ref chorusAccountEpoch) && OwnsCurrent && playback.HasMedia &&
        start < DurationSeconds && end <= DurationSeconds;
    public string ChorusLabel => HasChorus ? "官方高潮 " + Clock(chorusStart!.Value) : "";
    public double ChorusPositionSeconds => HasChorus ? chorusStart!.Value : double.NaN;
    public GridLength ChorusBefore => new(HasChorus ? chorusStart!.Value / DurationSeconds : 0, GridUnitType.Star);
    public GridLength ChorusAfter => new(HasChorus ? 1 - chorusStart!.Value / DurationSeconds : 1, GridUnitType.Star);

    private void ChorusBindings()
    {
        Changed(nameof(HasChorus)); Changed(nameof(ChorusLabel));
        Changed(nameof(ChorusPositionSeconds));
        Changed(nameof(ChorusBefore)); Changed(nameof(ChorusAfter));
    }
    private void ClearChorus()
    {
        chorusCancellation?.Cancel(); chorusCancellation = null;
        chorusKey = null; chorusStart = chorusEnd = null; ChorusBindings();
    }
    private void ChorusAccountChanged()
    {
        Interlocked.Increment(ref chorusAccountEpoch); // Fence before queued UI work.
        if (!Presentation.Dispatcher.HasShutdownStarted)
            _ = Presentation.Dispatcher.InvokeAsync(() => { ClearChorus(); if (!stopping) SyncChorus(); });
    }
    private void SyncChorus()
    {
        // No title matching or borrowing another edition/platform when the selected source lacks metadata.
        if (stopping || !OwnsCurrent || !playback.HasMedia || current is null ||
            musicRecovery?.Media?.Target is not { SourceKey: not null } target ||
            !PlaybackSong(current).TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        { if (chorusKey is not null) ClearChorus(); return; }
        var rows = sources.EnumerateArray().Where(row =>
            MusicDto.Text(row, "sourceKey") == target.SourceKey).ToArray();
        if (rows.Length != 1 || !rows[0].TryGetProperty("musicInfo", out var data)) { ClearChorus(); return; }
        string platform = target.Platform;
        string field = platform switch { "wy" or "tx" => "songmid", "kg" => "hash", _ => "" };
        if (field.Length == 0 || !data.TryGetProperty(field, out var idValue)) { ClearChorus(); return; }
        var id = idValue.ValueKind is JsonValueKind.String or JsonValueKind.Number ? idValue.ToString() : "";
        if (id.Length == 0) { ClearChorus(); return; }
        var durationValue = data.TryGetProperty("duration", out var d) ? d : default;
        if (durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetDouble(out var expected) ||
            !double.IsFinite(expected) || expected <= 0 || Math.Abs(expected - DurationSeconds) > 10)
        { if (chorusKey is not null) ClearChorus(); return; }
        var epoch = Interlocked.Read(ref chorusAccountEpoch);
        var key = PlaybackPresentationKey + "\0" + target.SourceKey + "\0" + epoch;
        var durableKey = PlaybackCache.MusicChorusCache.Key(target.AccountScope ?? "script:" + target.Installation,
            current.Id, platform, id, expected);
        if (key == chorusKey) { ChorusBindings(); return; }
        ClearChorus(); chorusKey = key; chorusIssuedEpoch = epoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        chorusCancellation = request;
        var work = Read(); eventWrites.Add(work); _ = Observe();
        async Task Read()
        {
            try
            {
                var cached = await persistentChorus.ReadAsync(durableKey, expected, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                if (cached is { } hit && hit.End <= DurationSeconds)
                { chorusStart = hit.Start; chorusEnd = hit.End; ChorusBindings(); return; }
                var reply = await Call("music.catalog.chorus", new { platform, id }, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                if (MusicDto.Text(reply, "platform") != platform ||
                    !string.Equals(MusicDto.Text(reply, "id"), id, StringComparison.OrdinalIgnoreCase)) return;
                var point = reply.GetProperty("point");
                if (point.ValueKind != JsonValueKind.Object) return;
                double start = point.GetProperty("start").GetDouble(), end = point.GetProperty("end").GetDouble();
                if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start &&
                    end <= expected && end <= DurationSeconds)
                {
                    chorusStart = start; chorusEnd = end; ChorusBindings();
                    await persistentChorus.PutAsync(durableKey, expected, start, end, request.Token);
                }
            }
            catch { /* Optional official metadata stays absent; never infer a fixed marker. */ }
            finally { if (ReferenceEquals(chorusCancellation, request)) chorusCancellation = null; request.Dispose(); }
        }
        async Task Observe() { try { await work; } finally { eventWrites.Remove(work); } }
    }
}
