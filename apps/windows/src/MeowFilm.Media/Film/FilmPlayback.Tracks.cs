using System.Text;

namespace MeowFilm.Media;

public enum FilmTrackKind { Video = 1, Audio = 2, Subtitle = 3 }

/// <summary>Index is the native stream identity, not the ordinal in a filtered list.</summary>
public sealed record FilmMediaTrack(int Index, FilmTrackKind Kind, string Codec,
    string Language, string Title, bool Selected);

/// <summary>Immutable current-session track snapshot. Null SubtitleIndex means subtitles are disabled.</summary>
public sealed record FilmMediaTracks(NativePlaybackScope Scope,
    IReadOnlyList<FilmMediaTrack> Video, IReadOnlyList<FilmMediaTrack> Audio,
    IReadOnlyList<FilmMediaTrack> Subtitles,
    int? VideoIndex, int? AudioIndex, int? SubtitleIndex);

/// <summary>Container chapter entry reported by the native film kernel.</summary>
public sealed record FilmMediaChapter(int Index, double StartSeconds, double EndSeconds, string Title);

/// <summary>Native diagnostic counters and status snapshot for --debug inspection.</summary>
public sealed record FilmKernelDiagnostics(
    ulong DroppedFrames, ulong AudioUnderruns, ulong AudioReconnects, ulong SubtitleErrors,
    double AvSyncDiffSeconds, double RenderFps,
    int VideoPacketQueue, int AudioPacketQueue, int SubtitlePacketQueue, int PictureQueue,
    bool HardwareFallback, bool AudioDegraded, bool ToneMapped, bool DisplayHdrEnabled,
    string DecodeFallbackReason, string LastNativeError);

public sealed partial class FilmPlayback
{
    private FilmMediaTracks? tracks;
    private IReadOnlyList<FilmMediaChapter> chapters = [];
    private FilmKernelDiagnostics? lastDiagnostics;

    public FilmMediaTracks? Tracks => tracks;
    public IReadOnlyList<FilmMediaChapter> Chapters => chapters;
    public FilmKernelDiagnostics? LastDiagnostics => lastDiagnostics;

    public Task<FilmMediaTracks> ReadTracksAsync(NativePlaybackScope expected, CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed(); token.ThrowIfCancellationRequested();
        var session = current;
        if (session is null || session.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        await commands.WaitAsync(token);
        try
        {
            RequireTrackSession(session);
            var value = await Task.Run(() => CaptureTracks(session));
            token.ThrowIfCancellationRequested(); RequireTrackSession(session);
            Set(ref tracks, value, nameof(Tracks));
            return value;
        }
        catch (Exception error)
        {
            await RetireCancelledAsync(session);
            if (error is OperationCanceledException) throw;
            throw Safe(error);
        }
        finally { commands.Release(); }
    });

    public Task<IReadOnlyList<FilmMediaChapter>> ReadChaptersAsync(NativePlaybackScope expected, CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed(); token.ThrowIfCancellationRequested();
        var session = current;
        if (session is null || session.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        await commands.WaitAsync(token);
        try
        {
            RequireTrackSession(session);
            var list = await Task.Run(() => CaptureChapters(session));
            token.ThrowIfCancellationRequested(); RequireTrackSession(session);
            Set(ref chapters, list, nameof(Chapters));
            return list;
        }
        catch (Exception error)
        {
            await RetireCancelledAsync(session);
            if (error is OperationCanceledException) throw;
            throw Safe(error);
        }
        finally { commands.Release(); }
    });

    public Task<FilmKernelDiagnostics> ReadDiagnosticsAsync(NativePlaybackScope expected, CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed(); token.ThrowIfCancellationRequested();
        var session = current;
        if (session is null || session.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        var diag = await Task.Run(() =>
            session.TryWithHandle(CaptureDiagnostics, out var result)
                ? result
                : throw new PlaybackException(PlaybackError.Superseded));
        if (ReferenceEquals(current, session))
        {
            Set(ref lastDiagnostics, diag, nameof(LastDiagnostics));
            UpdateDebugSnapshot();
        }
        return diag;
    });

    /// <summary>Non-blocking read-only diagnostic snapshot for --debug inspection without waiting on playback commands.</summary>
    public Task<FilmKernelDiagnostics?> ReadCurrentDiagnosticsAsync(CancellationToken token = default) => Dispatch(async () =>
    {
        if (disposing) return null;
        token.ThrowIfCancellationRequested();
        var session = current;
        if (session is null) return null;
        var diag = await Task.Run(() =>
            session.TryWithHandle(CaptureDiagnostics, out var result) ? result : null);
        if (diag is not null && ReferenceEquals(current, session))
        {
            Set(ref lastDiagnostics, diag, nameof(LastDiagnostics));
            UpdateDebugSnapshot();
        }
        return diag;
    });

    public Task SelectVideoTrackAsync(NativePlaybackScope scope, int index,
        CancellationToken token = default) => SelectTrackAsync(scope, FilmTrackKind.Video, index, token);

    public Task SelectAudioTrackAsync(NativePlaybackScope scope, int index,
        CancellationToken token = default) => SelectTrackAsync(scope, FilmTrackKind.Audio, index, token);

    /// <summary>Null disables subtitles; non-null values identify a real subtitle stream.</summary>
    public Task SelectSubtitleTrackAsync(NativePlaybackScope scope, int? index,
        CancellationToken token = default) => SelectTrackAsync(scope, FilmTrackKind.Subtitle, index, token);

    private Task SelectTrackAsync(NativePlaybackScope expected, FilmTrackKind kind, int? index,
        CancellationToken token) => Dispatch(async () =>
    {
        ThrowIfClosed(); token.ThrowIfCancellationRequested();
        var session = current;
        if (session is null || session.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        await commands.WaitAsync(token);
        try
        {
            RequireTrackSession(session);
            var value = await Task.Run(() => CaptureTracks(session));
            token.ThrowIfCancellationRequested(); RequireTrackSession(session);
            var available = kind switch
            {
                FilmTrackKind.Video => value.Video,
                FilmTrackKind.Audio => value.Audio,
                _ => value.Subtitles
            };
            if (index is { } selected && !available.Any(track => track.Index == selected) ||
                index is null && kind != FilmTrackKind.Subtitle)
                throw new PlaybackException(PlaybackError.InvalidTrack);

            // Never cancel the healthy playback session when a single caller's token cancels.
            await Task.Run(() =>
            {
                int rc = FilmKernelNative.SelectTrack(session.Handle, kind, index ?? -1);
                if (rc != 0)
                    throw new PlaybackException(session.ResolveError(rc));
                value = CaptureTracks(session);
            });
            RequireTrackSession(session);
            int? actual = kind switch
            {
                FilmTrackKind.Video => value.VideoIndex,
                FilmTrackKind.Audio => value.AudioIndex,
                _ => value.SubtitleIndex
            };
            if (actual != index)
                throw new PlaybackException(PlaybackError.TrackSelectionFailed);
            Set(ref tracks, value, nameof(Tracks));
            Publish(await Task.Run(() => FilmKernelNative.Read(session.Handle)));
        }
        catch (Exception error)
        {
            await RetireCancelledAsync(session);
            if (error is OperationCanceledException) throw;
            throw Safe(error);
        }
        finally { commands.Release(); }
    });

    private void RequireTrackSession(Session session)
    {
        ThrowIfClosed(); session.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(current, session)) throw new PlaybackException(PlaybackError.Superseded);
    }

    private static FilmMediaTracks CaptureTracks(Session session)
    {
        int count = FilmKernelNative.TrackCount(session.Handle);
        if (count < 0) throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        var video = new List<FilmMediaTrack>();
        var audio = new List<FilmMediaTrack>();
        var subtitles = new List<FilmMediaTrack>();
        var indices = new HashSet<int>();
        var selection = FilmKernelNative.Read(session.Handle);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            var track = FilmKernelNative.ReadTrack(session.Handle, ordinal);
            if (track.Index < 0 || !indices.Add(track.Index) || track.Kind is not (1 or 2 or 3))
                throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
            var kind = (FilmTrackKind)track.Kind;
            int selectedIndex = kind switch
            {
                FilmTrackKind.Video => selection.VideoTrack,
                FilmTrackKind.Audio => selection.AudioTrack,
                _ => selection.SubtitleTrack
            };
            var value = new FilmMediaTrack(track.Index, kind, ReadFixedUtf8(track.Codec, 32),
                ReadFixedUtf8(track.Language, 32), ReadFixedUtf8(track.Title, 128),
                track.Index == selectedIndex);
            (kind switch { FilmTrackKind.Video => video, FilmTrackKind.Audio => audio, _ => subtitles }).Add(value);
        }
        if (selection.VideoTrack != -1 && !video.Any(t => t.Index == selection.VideoTrack) ||
            selection.AudioTrack != -1 && !audio.Any(t => t.Index == selection.AudioTrack) ||
            selection.SubtitleTrack != -1 && !subtitles.Any(t => t.Index == selection.SubtitleTrack))
            throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        return new(session.Scope, video.AsReadOnly(), audio.AsReadOnly(), subtitles.AsReadOnly(),
            selection.VideoTrack == -1 ? null : selection.VideoTrack,
            selection.AudioTrack == -1 ? null : selection.AudioTrack,
            selection.SubtitleTrack == -1 ? null : selection.SubtitleTrack);
    }

    private static IReadOnlyList<FilmMediaChapter> CaptureChapters(Session session)
    {
        int count = FilmKernelNative.ChapterCount(session.Handle);
        if (count <= 0) return [];
        var result = new List<FilmMediaChapter>(count);
        for (int i = 0; i < count; i++)
        {
            var ch = FilmKernelNative.ReadChapter(session.Handle, i);
            result.Add(new FilmMediaChapter(ch.Index, ch.Start, ch.End, ReadFixedUtf8(ch.Title, 256)));
        }
        return result.AsReadOnly();
    }

    private static FilmKernelDiagnostics CaptureDiagnostics(IntPtr handle)
    {
        var d = FilmKernelNative.ReadDiagnostics(handle);
        return new FilmKernelDiagnostics(
            d.DroppedFrames, d.AudioUnderruns, d.AudioReconnects, d.SubtitleErrors,
            d.AvSyncDiff, d.RenderFps,
            d.VideoPacketQueue, d.AudioPacketQueue, d.SubtitlePacketQueue, d.PictureQueue,
            d.HardwareFallback != 0, d.AudioDegraded != 0, d.ToneMapped != 0, d.DisplayHdrEnabled != 0,
            ReadFixedUtf8(d.DecodeFallbackReason, 64),
            ReadFixedUtf8(d.LastNativeError, 128));
    }

    private static string ReadFixedUtf8(byte[] bytes, int capacity)
    {
        if (bytes is null || bytes.Length != capacity) throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = capacity;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
