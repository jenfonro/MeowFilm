namespace MeowFilm.Capabilities;

/// <summary>
/// Shared Layer-0 coordination state for catalog playback/save action generations and preparing card UI state.
/// Allows lower layers (Playback, Queue, Search) to invalidate or observe catalog action state without referencing Catalog.
/// </summary>
internal sealed class MusicCatalogActionState
{
    private long generation;
    private MusicCatalogCard? preparingCard;

    internal long Generation => generation;
    internal MusicCatalogCard? PreparingCard => preparingCard;

    internal event Action? Invalidated;

    internal void SetPreparingCard(MusicCatalogCard? card)
    {
        preparingCard = card;
        if (card is not null) card.IsPreparing = true;
    }

    internal void ClearPreparingCard()
    {
        if (preparingCard is { } card) card.IsPreparing = false;
        preparingCard = null;
    }

    internal void ClearPreparingCardIf(Func<MusicCatalogCard, bool> predicate)
    {
        if (preparingCard is { } card && predicate(card))
            ClearPreparingCard();
    }

    internal void Invalidate()
    {
        generation++;
        ClearPreparingCard();
        Invalidated?.Invoke();
    }
}
