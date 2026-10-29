using LibVLCSharp.Shared.Structures;

namespace MeowFilm.Media;

public sealed record NativePlaybackScope(string OwnerId, string Identity, long Generation);
public sealed record NativeMediaTrack(int Id, string Name);

/// <summary>
/// Audio/subtitle selection is read back from libVLC. Rate is libVLC's requested
/// rate, not proof of effective throughput, audio pitch or renderer support.
/// </summary>
public sealed record NativeMediaTracks(NativePlaybackScope Scope,
    IReadOnlyList<NativeMediaTrack> Audio, IReadOnlyList<NativeMediaTrack> Subtitles,
    int AudioId, int SubtitleId, double Rate, double SubtitleDelaySeconds)
{
    public bool EffectiveRateVerified => false;
}

public sealed partial class NativePlayback
{
    private NativeMediaTracks? _tracks;
    public NativeMediaTracks? Tracks => _tracks;

    public Task<NativeMediaTracks?> ReadTracksAsync(CancellationToken token = default)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return Task.FromException<NativeMediaTracks?>(Error(PlaybackError.DispatcherUnavailable));
        return _dispatcher.CheckAccess() ? ReadTracksCore(token)
            : _dispatcher.InvokeAsync(() => ReadTracksCore(token)).Task.Unwrap();
    }
    private async Task<NativeMediaTracks?> ReadTracksCore(CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        var current = _session;
        if (current is null) return null;
        var owner = _ownerId; var identity = _identity;
        await _commands.WaitAsync(token);
        try
        {
            if (!IsCurrent(current) || owner is null || identity is null) throw Error(PlaybackError.Superseded);
            var snapshot = await Task.Run(() => CaptureTracks(current, new(owner, identity, current.Generation)), token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(current)) throw Error(PlaybackError.Superseded);
            Set(ref _tracks, snapshot, nameof(Tracks));
            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw Error(PlaybackError.CommandFailed); }
        finally { _commands.Release(); }
    }

    public Task SelectAudioTrackAsync(NativePlaybackScope scope, int id, CancellationToken token = default) =>
        DispatchAsync(() => ChangeTrack(scope, (session, current) =>
        {
            if (!current.Audio.Any(track => track.Id == id)) throw Error(PlaybackError.InvalidTrack);
            if (!session.Player.SetAudioTrack(id)) throw Error(PlaybackError.TrackSelectionFailed);
        }, token));

    public Task SelectSubtitleTrackAsync(NativePlaybackScope scope, int id, CancellationToken token = default) =>
        DispatchAsync(() => ChangeTrack(scope, (session, current) =>
        {
            if (!current.Subtitles.Any(track => track.Id == id)) throw Error(PlaybackError.InvalidTrack);
            if (!session.Player.SetSpu(id)) throw Error(PlaybackError.TrackSelectionFailed);
        }, token));

    public Task SetRateAsync(NativePlaybackScope scope, double rate, CancellationToken token = default) =>
        DispatchAsync(() => ChangeTrack(scope, (session, _) =>
        {
            if (!double.IsFinite(rate) || rate is < 0.25 or > 4) throw Error(PlaybackError.InvalidRate);
            if (session.Player.SetRate((float)rate) != 0) throw Error(PlaybackError.RateUnsupported);
        }, token));

    public Task SetSubtitleDelayAsync(NativePlaybackScope scope, double seconds, CancellationToken token = default) =>
        DispatchAsync(() => ChangeTrack(scope, (session, current) =>
        {
            if (!double.IsFinite(seconds) || seconds is < -120 or > 120) throw Error(PlaybackError.InvalidSubtitleDelay);
            if (current.Subtitles.Count == 0 || !session.Player.SetSpuDelay((long)Math.Round(seconds * 1000000)))
                throw Error(PlaybackError.SubtitleUnavailable);
        }, token));

    private async Task ChangeTrack(NativePlaybackScope scope, Action<Session, NativeMediaTracks> change, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        var session = RequireScope(scope);
        await _commands.WaitAsync(token);
        try
        {
            if (RequireScope(scope) != session) throw Error(PlaybackError.Superseded);
            var snapshot = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var before = CaptureTracks(session, scope);
                change(session, before);
                return CaptureTracks(session, scope);
            }, token);
            token.ThrowIfCancellationRequested();
            if (RequireScope(scope) != session) throw Error(PlaybackError.Superseded);
            Set(ref _tracks, snapshot, nameof(Tracks));
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw Error(PlaybackError.CommandFailed); }
        finally { _commands.Release(); }
    }
    private Session RequireScope(NativePlaybackScope scope)
    {
        if (scope is null || _session is not { } session || !IsCurrent(session) ||
            scope.OwnerId != _ownerId || scope.Identity != _identity || scope.Generation != session.Generation)
            throw Error(PlaybackError.Superseded);
        return session;
    }
    private static NativeMediaTracks CaptureTracks(Session session, NativePlaybackScope scope) =>
        new(scope, TrackRows(session.Player.AudioTrackDescription), TrackRows(session.Player.SpuDescription),
            session.Player.AudioTrack, session.Player.Spu, session.Player.Rate, session.Player.SpuDelay / 1000000d);

    private static IReadOnlyList<NativeMediaTrack> TrackRows(TrackDescription[] values)
    {
        if (values.Length > 256 || values.Select(track => track.Id).Distinct().Count() != values.Length)
            throw Error(PlaybackError.TrackMetadataInvalid);
        var result = values.Select(track =>
        {
            string name = new((track.Name ?? "").Where(character => !char.IsControl(character)).Take(256).ToArray());
            return new NativeMediaTrack(track.Id, name.Length > 0 ? name : track.Id == -1 ? "关闭" : $"#{track.Id}");
        }).ToArray();
        return Array.AsReadOnly(result);
    }
}
