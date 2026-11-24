namespace MeowFilm.Capabilities;

public sealed partial class MusicCatalogController
{
    private readonly MusicCatalogOptionalReadService optionalReads;

    private IEnumerable<MusicCatalogCard> GetDisplayCardsForOptionalDemand()
    {
        foreach (var section in DisplayCatalogSections)
        {
            foreach (var item in section.Items) yield return item;
            if (section.Action is { } action) yield return action;
        }
        if (HasChannelTabs)
            foreach (var tab in ChannelTabs) yield return tab;
    }

    internal void OnLoadGenerationAdvanced()
    {
        optionalReads.RetireSurface();
        if (stopping) optionalReads.Retire();
        CancelCatalogLoad(homeContinuationCancellation);
        homeContinuationCancellation = null;
    }

    internal async ValueTask DisposeResourcesAsync()
    {
        await optionalReads.DisposeAsync().ConfigureAwait(false);
        await homeSnapshotStore.DisposeAsync().ConfigureAwait(false);
    }

    internal void RetireCatalogPreviewSurface() => optionalReads.RetireSurface();

    internal void UpdateCatalogVisibleCards(IEnumerable<MusicCatalogCard> cards) =>
        optionalReads.UpdateVisibleCards(cards);
}
