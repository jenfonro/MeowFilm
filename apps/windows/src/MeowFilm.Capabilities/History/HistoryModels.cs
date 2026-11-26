using System.ComponentModel;

namespace MeowFilm.Capabilities;

public sealed class HistoryTab : INotifyPropertyChanged
{
    public HistoryTab(string id, string label, bool selected)
    {
        Id = id;
        Label = label;
        Selected = selected;
    }

    public string Id { get; }
    public string Label { get; }
    public string AccessibleName => Label;
    public bool Selected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Select(bool value)
    {
        if (Selected == value) return;
        Selected = value;
        PropertyChanged?.Invoke(this, new(nameof(Selected)));
    }
}

internal sealed record HistoryNovelCardItem(
    NovelSourceService Sources,
    NovelEntry Entry,
    string BadgeText,
    string SubtitleText) : ICatalogCardDisplay
{
    public string Id => Entry.BookId;
    public string Title => Entry.Book.Name;
    public string Poster => "";
    public string Score => "";
    public bool HasScore => false;
    public string TextBadge => BadgeText;
    public bool HasTextBadge => BadgeText.Length > 0;
    public string Subtitle => SubtitleText;
    public bool HasSubtitle => SubtitleText.Length > 0;
    public bool ShowPlayMark => false;
    public NovelBook? NovelBook => Entry.Book;
    public NovelSourceService? NovelSources => Sources;

    public bool HasSameDisplay(ICatalogCardDisplay other) =>
        other is HistoryNovelCardItem card &&
        ReferenceEquals(Sources, card.Sources) &&
        Entry.BookId == card.Entry.BookId &&
        BadgeText == card.BadgeText &&
        SubtitleText == card.SubtitleText &&
        Entry.Book.ContentEquals(card.Entry.Book);
}

public sealed class HistoryHomeSection : CatalogHomeSection
{
    private readonly string actionText;

    internal HistoryHomeSection(string key, string title, string emptyText, string actionText = "")
        : base(key, title, defaultFailedText: "加载失败", emptyText: emptyText)
    {
        this.actionText = actionText;
        MarkAdmitted();
    }

    public override bool ShowRefresh => false;
    public override string ActionText => actionText;
    public override bool ShowAction => actionText.Length > 0 && HasItems;
    public override bool CanAction => ShowAction && State != CatalogSectionState.Loading;

    internal void PresentItems(IReadOnlyList<ICatalogCardDisplay> items) => CompleteItems(items);
}

public sealed class HistoryDataSection : CatalogDataSection
{
    private readonly string actionText;

    internal HistoryDataSection(string key, string title, string emptyText, string actionText = "")
        : base(key, title, [], defaultFailedText: "加载失败", emptyText: emptyText)
    {
        this.actionText = actionText;
    }

    public override bool ShowRefresh => false;
    public override string ActionText => actionText;
    public override bool ShowAction => actionText.Length > 0 && HasItems;
    public override bool CanAction => ShowAction && State != CatalogSectionState.Loading;

    internal void PresentItems(IReadOnlyList<ICatalogCardDisplay> items, bool more) =>
        ReplaceAllItems(items, more);

    internal void AppendPageItems(IReadOnlyList<ICatalogCardDisplay> items, bool more) =>
        AppendUniqueItems(items, _ => more);
}
