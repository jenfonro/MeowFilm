using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    public Task PlayCatalogCardAsync(MusicCatalogCard card)
    {
        if (!IsCatalogPage || !IsCatalogHome && ctx.LoadCancellation is not null ||
            !CanCatalogBrowse || !card.CanPlay || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("play", 0, entry: card);
        if (card.Kind is not ("daily" or "album")) return OpenCatalogCardAsync(card);
        var cardSource = new MusicQueueSourceRecord(card.ContentKey, card.Title, card.Kind, card.Platform);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var reply = await MusicCatalogDispatcher.InvokeCardReadAsync(application, card, previewLimit: false, signal);
            if (!currentAction()) return;
            var songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(s => s.Items)
                .Where(c => c.Song is not null).Select(c => c.Song!.Record).ToArray();
            MusicDto.Require(songs.Length > 0);
            await RunPlaybackActionAsync(async _ =>
            {
                if (!currentAction()) return;
                RequireCatalogContent();
                var queue = await application.GetQueueAsync(0, 1, signal);
                var version = queue.Version;
                if (!currentAction()) return;
                try
                {
                    playbackController.AdvanceQueueEpoch();
                    var result = await application.ReplaceQueueAsync(songs, 0, null, cardSource, ct: signal);
                    MusicDto.Require(result.Version > version);
                    await playbackController.ReadQueue(lifetime.Token);
                }
                catch (OperationCanceledException) { return; }
                catch { playbackController.MarkQueueUnsaved(); throw; }
                if (currentAction()) await StartCatalogCurrent(signal);
            }, supersede: true);
        }, card: card);
    }
}
