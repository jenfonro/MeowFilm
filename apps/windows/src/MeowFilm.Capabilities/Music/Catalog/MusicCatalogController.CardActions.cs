namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private Dictionary<string, object?> CardReadParameters(MusicCatalogCard card)
    {
        var parameters = new Dictionary<string, object?> { ["platform"] = card.Platform };
        if (card.Kind == "daily") parameters["branch"] = CardDailyBranch(card);
        else if (card.Kind == "fm")
        {
            parameters["preview"] = true;
            if (card.Action is { } action)
                foreach (var name in new[] { "mode", "submode", "ruleId", "tabTagId" })
                    if (MusicCatalogDto.Text(action, name) is { Length: > 0 } value) parameters[name] = value;
        }
        else
        {
            parameters["id"] = card.ActionId;
            if (card.Kind == "playlist") { parameters["page"] = 1; parameters["limit"] = 3; }
            if (card.Locator is { } locator) parameters["personal"] = locator;
        }
        return parameters;
    }

    private static string CardDailyBranch(MusicCatalogCard card) =>
        card.Action is { } action && MusicCatalogDto.Text(action, "branch") == "style" ? "style" : "default";

    public Task PlayCatalogCardAsync(MusicCatalogCard card)
    {
        if (!IsCatalogPage || !IsCatalogHome && ctx.LoadCancellation is not null ||
            !CanCatalogBrowse || !card.CanPlay || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("play", 0, entry: card);
        if (card.Kind is not ("daily" or "album")) return OpenCatalogCardAsync(card);
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            RequireCatalogContent();
            var parameters = CardReadParameters(card);
            parameters.Remove("page"); parameters.Remove("limit");
            var reply = await Call("music.catalog." + card.Kind, parameters, signal);
            if (!currentAction()) return;
            var songs = MusicCatalogDto.Read(reply, card.Platform, card.Kind, "").SelectMany(s => s.Items)
                .Where(c => c.Song is not null).Select(c => c.Song!.Raw).ToArray();
            MusicDto.Require(songs.Length > 0);
            await RunPlaybackActionAsync(async _ =>
            {
                if (!currentAction()) return;
                RequireCatalogContent();
                var queue = await Call("music.queue.get", new { offset = 0, limit = 1 }, signal);
                var version = MusicDto.Integer(queue, "version");
                if (!currentAction()) return;
                try
                {
                    playbackController.AdvanceQueueEpoch();
                    var result = await CallSettledReceipt("music.queue.replace", new { songs, index = 0, context = new
                    { queueSource = new { id = card.ContentKey, title = card.Title, kind = card.Kind, platform = card.Platform } } }, signal);
                    MusicDto.Require(MusicDto.Integer(result, "version") > version);
                    await playbackController.ReadQueue(lifetime.Token);
                    if (currentAction()) await StartCatalogCurrent(signal);
                }
                catch (OperationCanceledException) { }
                catch { playbackController.MarkQueueUnsaved(); throw; }
            }, supersede: true);
        }, card: card);
    }
}
