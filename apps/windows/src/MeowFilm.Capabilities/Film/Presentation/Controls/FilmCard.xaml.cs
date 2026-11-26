using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class CatalogCardActivatedEventArgs(ICatalogCardDisplay item) : EventArgs
{
    public ICatalogCardDisplay Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public sealed class FilmCardActivatedEventArgs(FilmCardItem item) : EventArgs
{
    public FilmCardItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public partial class FilmCard : UserControl
{
    public event EventHandler<CatalogCardActivatedEventArgs>? CardActivated;
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    private INotifyPropertyChanged? observed;
    private object? boundContext;
    public ICatalogCardDisplay? Item => DataContext as ICatalogCardDisplay;
    public string CardTitle => Item?.Title ?? "";
    public string PosterAddress => Item?.NovelBook is null ? Item?.Poster ?? "" : "";
    public NovelBook? NovelBook => Item?.NovelBook;
    public NovelSourceService? NovelSources => Item?.NovelSources;
    public Visibility FilmPosterVisibility => Item?.NovelBook is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NovelPosterVisibility => Item?.NovelBook is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlayMarkVisibility => Item?.ShowPlayMark == true ? Visibility.Visible : Visibility.Collapsed;
    public string CardScore => Item?.Score ?? "";
    public Visibility ScoreBadgeVisibility => Item?.HasScore == true ? Visibility.Visible : Visibility.Collapsed;
    public string CardTextBadge => Item?.TextBadge ?? "";
    public Visibility TextBadgeVisibility => Item is { HasTextBadge: true, HasScore: false } ? Visibility.Visible : Visibility.Collapsed;
    public string CardSubtitle => Item?.Subtitle ?? "";
    public Visibility SubtitleVisibility => Item?.HasSubtitle == true ? Visibility.Visible : Visibility.Collapsed;

    public FilmCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe();
        Loaded += (_, _) => Observe();
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            if (observed is not null) observed.PropertyChanged -= Changed;
            observed = null;
        };
        PosterFrame.SizeChanged += (_, args) => TextBadge.MaxWidth = args.NewSize.Width * .7;
        SizeChanged += (_, args) => SiteLabel.MaxWidth = args.NewSize.Width;
    }

    private void Observe()
    {
        var current = DataContext;
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = IsLoaded ? current as INotifyPropertyChanged : null;
        if (observed is not null) observed.PropertyChanged += Changed;
        if (!ReferenceEquals(boundContext, current))
        {
            boundContext = current;
            Bindings.Update();
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (Item is not { } item) return;
        switch (e.PropertyName)
        {
            case nameof(ICatalogCardDisplay.Title):
                TitleText.Text = item.Title;
                AutomationProperties.SetName(HitButton, item.Title);
                break;
            case nameof(ICatalogCardDisplay.Poster):
                if (item.NovelBook is null) Poster.Address = item.Poster;
                break;
            case nameof(ICatalogCardDisplay.NovelBook) or nameof(ICatalogCardDisplay.NovelSources):
                BookPoster.Sources = item.NovelSources;
                BookPoster.Book = item.NovelBook;
                Poster.Visibility = FilmPosterVisibility;
                BookPoster.Visibility = NovelPosterVisibility;
                break;
            case nameof(ICatalogCardDisplay.Score):
                ScoreText.Text = item.Score;
                break;
            case nameof(ICatalogCardDisplay.HasScore):
                ScoreBadge.Visibility = ScoreBadgeVisibility;
                TextBadge.Visibility = TextBadgeVisibility;
                break;
            case nameof(ICatalogCardDisplay.TextBadge):
                TextBadgeText.Text = item.TextBadge;
                break;
            case nameof(ICatalogCardDisplay.HasTextBadge):
                TextBadge.Visibility = TextBadgeVisibility;
                break;
            case nameof(ICatalogCardDisplay.Subtitle):
                Subtitle.Text = item.Subtitle;
                break;
            case nameof(ICatalogCardDisplay.HasSubtitle):
                SiteLabel.Visibility = SubtitleVisibility;
                break;
            case null or "":
                Bindings.Update();
                break;
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Item is not { } item) return;
        CardActivated?.Invoke(this, new(item));
        if (item is IFilmCardDisplay film)
            Activated?.Invoke(this, new(film.ToCard()));
    }
}
