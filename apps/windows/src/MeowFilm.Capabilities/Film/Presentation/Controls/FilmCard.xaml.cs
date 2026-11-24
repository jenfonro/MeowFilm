using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

public sealed class FilmCardActivatedEventArgs(FilmCardItem item) : EventArgs
{
    public FilmCardItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public partial class FilmCard : UserControl
{
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    private INotifyPropertyChanged? observed;
    private object? boundContext;
    public IFilmCardDisplay? Item => DataContext as IFilmCardDisplay;
    public Visibility TextBadgeVisibility => Item is { HasTextBadge: true, HasScore: false } ? Visibility.Visible : Visibility.Collapsed;

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
            case nameof(IFilmCardDisplay.Title):
                TitleText.Text = item.Title;
                AutomationProperties.SetName(HitButton, item.Title);
                break;
            case nameof(IFilmCardDisplay.Poster):
                Poster.Address = item.Poster;
                break;
            case nameof(IFilmCardDisplay.Score):
                ScoreText.Text = item.Score;
                break;
            case nameof(IFilmCardDisplay.HasScore):
                ScoreBadge.Visibility = item.HasScore ? Visibility.Visible : Visibility.Collapsed;
                TextBadge.Visibility = TextBadgeVisibility;
                break;
            case nameof(IFilmCardDisplay.TextBadge):
                TextBadgeText.Text = item.TextBadge;
                break;
            case nameof(IFilmCardDisplay.HasTextBadge):
                TextBadge.Visibility = TextBadgeVisibility;
                break;
            case nameof(IFilmCardDisplay.Subtitle):
                Subtitle.Text = item.Subtitle;
                break;
            case nameof(IFilmCardDisplay.HasSubtitle):
                SiteLabel.Visibility = item.HasSubtitle ? Visibility.Visible : Visibility.Collapsed;
                break;
            case null or "":
                Bindings.Update();
                break;
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Item?.ToCard() is { } item)
            Activated?.Invoke(this, new(item));
    }
}
