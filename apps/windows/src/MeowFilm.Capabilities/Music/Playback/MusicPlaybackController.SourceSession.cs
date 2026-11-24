using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private void AcceptOfficialSong(SongRow song, MusicOnlineTarget? target)
    {
        if (target?.RecordingSong is null) return;
        if (current?.EntryId == song.EntryId && current?.Id == song.Id) RefreshCurrentArtwork();
    }

    private MusicSongRecord FeedbackSong(SongRow song)
    {
        var snapshot = song.Record;
        if (musicRecovery?.Media?.Target is not { Official: true, SourceKey: not null } target) return snapshot;
        return snapshot with
        {
            Sources = snapshot.Sources
                .Where(row => row.Platform != target.Platform || row.SourceKey == target.SourceKey)
                .ToArray()
        };
    }

    private async Task<MusicSourceSession> CurrentSourceSession(MusicRecovery state, CancellationToken token)
    {
        var previous = state.Session!;
        if (previous.Revision == sourceService.SelectionSourcesRevision && previous.Signature == settings.SavedQualitySignature) return previous;
        var session = await sourceService.BeginSessionAsync(state.Song, token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRecovery(state)) throw new OperationCanceledException(token);
        session.Inherit(previous);
        state.RetiredSessions.Add(previous);
        state.Session = session;
        state.RecoveryDisabled = false;
        return session;
    }
}
