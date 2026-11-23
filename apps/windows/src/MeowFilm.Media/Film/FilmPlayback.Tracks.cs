using System.Text;

namespace MeowFilm.Media;

public enum FilmTrackKind { Audio = 2, Subtitle = 3 }

/// <summary>Index is the native stream identity, not the ordinal in a filtered list.</summary>
public sealed record FilmMediaTrack(int Index, FilmTrackKind Kind, string Codec,
    string Language, string Title, bool Selected);

/// <summary>Immutable current-session snapshot. Null SubtitleIndex means subtitles are disabled.</summary>
public sealed record FilmMediaTracks(NativePlaybackScope Scope,
    IReadOnlyList<FilmMediaTrack> Audio, IReadOnlyList<FilmMediaTrack> Subtitles,
    int? AudioIndex, int? SubtitleIndex);

public sealed partial class FilmPlayback
{
    private FilmMediaTracks? tracks;
    public FilmMediaTracks? Tracks => tracks;

    public Task<FilmMediaTracks?> ReadTracksAsync(CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed(); token.ThrowIfCancellationRequested();
        var session = current;
        if (session is null) return null;
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
            var available = kind == FilmTrackKind.Audio ? value.Audio : value.Subtitles;
            if (index is { } selected && !available.Any(track => track.Index == selected) ||
                index is null && kind == FilmTrackKind.Audio)
                throw new PlaybackException(PlaybackError.InvalidTrack);
            // Once native work starts, cancellation must interrupt the command's IO.
            using var cancellation = token.Register(session.Cancel);
            await Task.Run(() =>
            {
                if (FilmKernelNative.SelectTrack(session.Handle, kind, index ?? -1) != 0)
                    throw new PlaybackException(session.Error ?? PlaybackError.TrackSelectionFailed);
                value = CaptureTracks(session);
            });
            token.ThrowIfCancellationRequested(); RequireTrackSession(session);
            if ((kind == FilmTrackKind.Audio ? value.AudioIndex : value.SubtitleIndex) != index)
                throw new PlaybackException(PlaybackError.TrackSelectionFailed);
            Set(ref tracks, value, nameof(Tracks));
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

    private FilmMediaTracks CaptureTracks(Session session)
    {
        int count = FilmKernelNative.TrackCount(session.Handle);
        if (count < 0) throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        var audio = new List<FilmMediaTrack>();
        var subtitles = new List<FilmMediaTrack>();
        var indices = new HashSet<int>();
        var selection = FilmKernelNative.Read(session.Handle);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            var track = FilmKernelNative.ReadTrack(session.Handle, ordinal);
            if (track.Index < 0 || !indices.Add(track.Index) || track.Kind is not (1 or 2 or 3))
                throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
            if (track.Kind == 1) continue;
            var kind = (FilmTrackKind)track.Kind;
            var value = new FilmMediaTrack(track.Index, kind, TrackText(track.Codec, 32),
                TrackText(track.Language, 32), TrackText(track.Title, 128),
                track.Index == (kind == FilmTrackKind.Audio ? selection.AudioTrack : selection.SubtitleTrack));
            (kind == FilmTrackKind.Audio ? audio : subtitles).Add(value);
        }
        if (selection.AudioTrack != -1 && !audio.Any(track => track.Index == selection.AudioTrack) ||
            selection.SubtitleTrack != -1 && !subtitles.Any(track => track.Index == selection.SubtitleTrack))
            throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        return new(session.Scope, audio.AsReadOnly(), subtitles.AsReadOnly(),
            selection.AudioTrack == -1 ? null : selection.AudioTrack,
            selection.SubtitleTrack == -1 ? null : selection.SubtitleTrack);
    }
    private static string TrackText(byte[] bytes, int capacity)
    {
        if (bytes is null || bytes.Length != capacity) throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
