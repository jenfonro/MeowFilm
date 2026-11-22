using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class FilmCardActivatedEventArgs(FilmCardItem item) : EventArgs
{
    public FilmCardItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public partial class FilmCard : UserControl
{
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    private INotifyPropertyChanged? observed;
    private IDisposable? theme;
    public IFilmCardDisplay? Item => DataContext as IFilmCardDisplay;
    public Visibility TextBadgeVisibility => Item is { HasTextBadge: true, HasScore: false } ? Visibility.Visible : Visibility.Collapsed;
    public FilmCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe();
        Loaded += (_, _) => { theme = DesktopTheme.Observe(this, Refresh); Observe(); };
        Unloaded += (_, _) => { theme?.Dispose(); theme = null; if (observed is not null) observed.PropertyChanged -= Changed; observed = null; };
        PosterFrame.SizeChanged += (_, args) => TextBadge.MaxWidth = args.NewSize.Width * .7;
        SizeChanged += (_, args) => SiteLabel.MaxWidth = args.NewSize.Width;
    }
    private void Observe()
    {
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = DataContext as INotifyPropertyChanged;
        if (observed is not null) observed.PropertyChanged += Changed;
        Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        Bindings?.Update();
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        TextBadge.Background = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent") : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 249, 115, 22));
        if (TextBadge.Child is TextBlock text)
            text.Foreground = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.OnAccent") : new SolidColorBrush(Microsoft.UI.Colors.White);
        SiteLabel.Background = contrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.Window) : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 250, 252));
        SiteLabel.BorderBrush = contrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 232, 240));
        if (IsLoaded) Subtitle.Foreground = (Brush)DesktopVisual.Resource(this, contrast ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Muted");
        ScoreBadge.Background = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent") : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 72, 153));
        ((TextBlock)ScoreBadge.Child).Foreground = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.OnAccent") : new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Item?.ToCard() is { } item)
            Activated?.Invoke(this, new(item));
    }
}
